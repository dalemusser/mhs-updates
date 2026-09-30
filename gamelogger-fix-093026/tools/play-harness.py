# Browser harness (Python Playwright; use stratahub/tests/e2e/venv): signs in a test member on the
# dev site, blocks the log host, drives Unit 1 into gameplay, reads the game's saved queue from
# IndexedDB, counts log requests, and forces a "none" verdict to show the notice.
# Written for the 2026-09-28 StrataHub repair test, where it also patched the play page and served
# the working-copy mhs-steplog.js (PATCH / the two ctx.route calls). When testing a game build as
# deployed, set PATCH['on'] = False and drop the mhs-steplog route. Set TEST_MEMBER_LOGIN_ID
# (see memory: dev-mhs-test-member). Note: in this headless setup the game advances slowly; the
# playwright-cli recipe in the plan (block host, Space, START GAME, Space, WASD) is more reliable.
import difflib, json, pathlib, re, subprocess, sys, time
from playwright.sync_api import sync_playwright

REPO = pathlib.Path('/Users/dale/Documents/catchupstratahub/stratahub')
TPL = 'internal/app/features/missionhydrosci/templates/missionhydrosci_play.gohtml'
old_tpl = subprocess.run(['git', 'show', 'HEAD:' + TPL], cwd=REPO, capture_output=True, text=True, check=True).stdout.splitlines(keepends=True)
new_tpl = (REPO / TPL).read_text().splitlines(keepends=True)
new_steplog = (REPO / 'internal/app/resources/assets/js/mhs-steplog.js').read_text()
BASE = 'https://dev.adroit.games'
HASH = 'd161fc68e18bf3e91c0f5688fe046e75'  # md5 of BASE + '/missionhydrosci/play'

def log(*a):
    print(time.strftime('%H:%M:%S'), *a, flush=True)

patch_report = {}
PATCH = {'on': True}
def patch(html):
    if not PATCH['on']:
        patch_report.update(applied=0, skipped='(patching off)')
        return html
    applied, skipped = 0, []
    sm = difflib.SequenceMatcher(a=old_tpl, b=new_tpl, autojunk=False)
    for tag, i1, i2, j1, j2 in reversed(sm.get_opcodes()):
        if tag == 'equal':
            continue
        done = False
        for ctx in range(0, 6):  # grow the preceding context until the old text is unique
            a = ''.join(old_tpl[max(0, i1 - ctx):i2]); b = ''.join(old_tpl[max(0, i1 - ctx):i1]) + ''.join(new_tpl[j1:j2])
            if a and html.count(a) == 1:
                html = html.replace(a, b); applied += 1; done = True; break
        if not done:
            skipped.append(''.join(old_tpl[i1:i2]).strip()[:90] or ('insert after: ' + old_tpl[i1 - 1].strip()[:80]))
    patch_report.update(applied=applied, skipped=skipped)
    return html

STORE_JS = """() => new Promise(resolve => {
  indexedDB.databases().then(list => {
    if (!list.some(d => d.name === '/idbfs')) return resolve('none');
    const r = indexedDB.open('/idbfs');
    r.onsuccess = () => { const db = r.result;
      if (!db.objectStoreNames.contains('FILE_DATA')) { db.close(); return resolve('none'); }
      const g = db.transaction('FILE_DATA', 'readonly').objectStore('FILE_DATA').get('/idbfs/%s/PlayerPrefs');
      g.onsuccess = () => { db.close(); if (!g.result) return resolve('none'); const u8 = g.result.contents; let s = ''; for (let i = 0; i < u8.length; i++) s += String.fromCharCode(u8[i]); resolve(btoa(s)); };
      g.onerror = () => { db.close(); resolve('err'); };
    };
    r.onerror = () => resolve('err');
  });
})""" % HASH

def store_summary(page):
    import base64
    b = page.evaluate(STORE_JS)
    if b in ('none', 'err'):
        return b
    raw = base64.b64decode(b).decode('utf-8', 'replace')
    j = raw.find('{"logs":')
    if j < 0:
        return 'no cache'
    logs = json.JSONDecoder().raw_decode(raw[j:])[0]['logs']
    return {'n': len(logs), 'empties': sum(1 for x in logs if x == '{}'), 'head': logs[0][:40] if logs else None}

def steps(page, which=('storage', 'launch')):
    try:
        es = page.evaluate("() => (window.__mhsStepLog && window.__mhsStepLog.entries || []).map(e => [e.at, e.step, e.state, e.msg])")
    except Exception as e:
        return ['(no step log: %s)' % e]
    return [e for e in es if e[1] in which]

def wait_rendering(page, timeout=240):
    t0 = time.time(); since = time.strftime('%Y-%m-%dT%H:%M:%S', time.gmtime(t0 - 1))
    while time.time() - t0 < timeout:
        try:
            if page.evaluate("s => !!(window.__mhsStepLog && window.__mhsStepLog.entries.some(e => /rendering/.test(e.msg) && e.at >= s))", since):
                return time.time() - t0
        except Exception:
            pass
        time.sleep(2)
    return None

posts = []
def on_response(resp):
    if 'log.adroit.games/api/log/submit' in resp.url:
        posts.append((time.strftime('%H:%M:%S'), resp.status))
def on_failed(req):
    if 'log.adroit.games/api/log/submit' in req.url:
        posts.append((time.strftime('%H:%M:%S'), 'failed'))

def play_into_game(page, seconds_wasd=16):
    page.click('#unity-canvas'); page.keyboard.press('Space'); time.sleep(25)
    page.mouse.click(1010, 247); time.sleep(15)
    page.click('#unity-canvas'); page.keyboard.press('Space'); time.sleep(8)
    end = time.time() + seconds_wasd
    while time.time() < end:
        for k in 'wasd':
            page.keyboard.down(k); time.sleep(2); page.keyboard.up(k)

with sync_playwright() as p:
    browser = p.chromium.launch(headless=True)
    ctx = browser.new_context(service_workers='block', viewport={'width': 1280, 'height': 720})
    ctx.route(re.compile(r'.*/assets/js/mhs-steplog\.js.*'), lambda route: route.fulfill(status=200, body=new_steplog, content_type='application/javascript'))
    def play_route(route):
        resp = route.fetch()
        route.fulfill(response=resp, body=patch(resp.text()))
    ctx.route(re.compile(r'.*/missionhydrosci/play/unit\d+(\?.*)?$'), play_route)
    page = ctx.new_page()
    ctx.on('response', on_response); ctx.on('requestfailed', on_failed)
    page.on('console', lambda m: m.type == 'error' and log('console error:', m.text[:160]))
    page.on('pageerror', lambda e: log('PAGE ERROR:', str(e)[:200]))

    page.goto(BASE + '/login'); page.get_by_role('textbox', name='Login ID').fill('TEST_MEMBER_LOGIN_ID')
    page.get_by_role('button', name='Login').click(); page.wait_for_load_state('load'); time.sleep(3); log('signed in at', page.url)

    # Phase 1: the page as deployed today (unpatched), log host blocked: the wedge.
    PATCH['on'] = False
    blocked = lambda route: route.abort('connectionrefused')
    ctx.route(re.compile(r'https://log\.adroit\.games/.*'), blocked)
    page.goto(BASE + '/missionhydrosci/play/unit1')
    log('phase 1 (unpatched) rendering after', wait_rendering(page), 's')
    play_into_game(page)
    t0 = time.time()
    while time.time() - t0 < 150:
        st = store_summary(page)
        if isinstance(st, dict) and st['empties'] and st['head'] == '{}':
            break
        for k in 'wasd':
            page.keyboard.down(k); time.sleep(2); page.keyboard.up(k)
    log('phase 1 posts:', posts); log('phase 1 store:', store_summary(page))
    ctx.unroute(re.compile(r'https://log\.adroit\.games/.*'), blocked)
    page.goto(BASE + '/missionhydrosci/units'); time.sleep(3)
    log('after leaving, store:', store_summary(page))

    # Phase 2: the new page, host reachable: repair before the game, then the backlog drains.
    PATCH['on'] = True
    posts.clear()
    nav = time.strftime('%Y-%m-%dT%H:%M:%S', time.gmtime(time.time() - 1))
    page.goto(BASE + '/missionhydrosci/play/unit1'); log('patch:', json.dumps(patch_report))
    log('phase 2 rendering after', wait_rendering(page), 's')
    for e in steps(page):
        if e[0] >= nav: log('  step', e[0][11:23], e[1], e[2], e[3][:170])
    time.sleep(3); log('posts before any input:', posts)
    page.click('#unity-canvas'); page.keyboard.press('Space'); time.sleep(5)
    t0 = time.time()
    while time.time() - t0 < 90:
        st = store_summary(page)
        if isinstance(st, dict) and st['n'] == 0:
            break
        for k in 'wasd':
            page.keyboard.down(k); time.sleep(2); page.keyboard.up(k)
    log('phase 2 drained after', round(time.time() - t0), 's')
    st = [x for _, x in posts]
    log('phase 2 posts:', st.count(201), 'accepted,', st.count(400), 'refused, total', len(st))
    log('phase 2 store:', store_summary(page))

    # Phase 3: the new page, clean store, the heartbeat verdict forced to 'none': the notice.
    def hb_route(route):
        resp = route.fetch()
        try:
            d = resp.json(); d['logs'] = 'none'; body = json.dumps(d)
        except Exception:
            body = '{"logs":"none"}'
        route.fulfill(response=resp, body=body, content_type='application/json')
    ctx.route(re.compile(r'.*/missionhydrosci/api/steplog/[0-9a-f]+/heartbeat.*'), hb_route)
    page.goto(BASE + '/missionhydrosci/units'); time.sleep(3)
    nav = time.strftime('%Y-%m-%dT%H:%M:%S', time.gmtime(time.time() - 1))
    page.goto(BASE + '/missionhydrosci/play/unit1')
    log('phase 3 rendering after', wait_rendering(page), 's')
    log('phase 3 storage steps:', json.dumps([e[3][:140] for e in steps(page, ('storage',)) if e[0] >= nav]))
    t0 = time.time(); shown = None
    while time.time() - t0 < 75:
        shown = page.evaluate("() => { var n = document.getElementById('mhs-logging-notice'); return n && !n.hidden ? [document.getElementById('mhs-ln-text').textContent, document.getElementById('mhs-ln-reason').textContent, document.getElementById('mhs-ln-how').innerText.slice(0, 600)] : null; }")
        if shown: break
        time.sleep(3)
    log('phase 3 notice after', round(time.time() - t0), 's:', json.dumps(shown))
    log('offline text:', page.evaluate("() => document.getElementById('mhs-off-text').textContent"))
    page.goto(BASE + '/missionhydrosci/units'); time.sleep(2)
    browser.close()
log('done')
