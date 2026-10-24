// Read-only, streaming. Every mhs save in player_states (the service keeps the last 5 per student): per student
// the sequence of scene names and checkpoint values, close pairs, regressions (a later save behind an earlier
// one), sizes. One cursor pass; only small derived rows are kept. Ids shortened in the output.
const S = db.getSiblingDB('stratasave');
const short = s => '…' + String(s||'').slice(-6);
const unitNo = scene => { const m = /Unit (\d)/.exec(scene||''); return m ? +m[1] : -1; };
const rows = []; const cpKeys = {}; const recStats = {};
S.player_states.find({game: 'mhs'}).sort({user_id: 1, timestamp: 1}).batchSize(50).forEach(d => {
  const sd = d.save_data || {}; const list = sd.m_list || [];
  let cp = null, ncp = 0, dlg = 0, total = 0, biggestKey = '', biggest = 0;
  for (const r of list) {
    const len = String(r.data||'').length; total += len;
    if (len > biggest) { biggest = len; biggestKey = r.key; }
    const base = String(r.key||'').replace(/^.*?(DialogueSystemSaver|CheckpointSaver|PositionSaver|MultiEnabledSaver|MultiActiveSaver|EnabledSaver|ActiveSaver|DoorLockSaver|PuzzleSaver|MaterialColorSaver|LightColorIntensitySaver|CustomWaypointSaver|MultiPositionSaver|SettingsSaver)$/, '$1');
    const st = recStats[base] = recStats[base] || {n: 0, bytes: 0, max: 0}; st.n++; st.bytes += len; if (len > st.max) st.max = len;
    if (/CheckpointSaver$/.test(r.key||'')) { ncp++; cpKeys[r.key] = (cpKeys[r.key]||0) + 1; if (cp === null) { try { cp = JSON.parse(r.data).id; } catch (e) { cp = '?'; } } }
  }
  rows.push({u: d.user_id, t: d.timestamp, scene: sd.m_sceneName, cp, ncp, nrec: list.length, size: total + 200 + list.length * 40, dlg: ((list.find(r => /DialogueSystemSaver$/.test(r.key||''))||{}).data||'').length, biggestKey, biggest});
});
print('saves read: ' + rows.length);
print('checkpoint saver keys seen: ' + JSON.stringify(cpKeys));
print('saves with 0 / 1 / >1 checkpoint records: ' + [0,1].map(k => rows.filter(r => r.ncp === k).length).join(' / ') + ' / ' + rows.filter(r => r.ncp > 1).length);
const sizes = rows.map(r => r.size).sort((a,b) => a-b); const pct = p => sizes[Math.min(sizes.length-1, Math.floor(p*sizes.length))];
print('approx save_data bytes (sum of record strings): min=' + pct(0) + ' p50=' + pct(0.5) + ' p90=' + pct(0.9) + ' p99=' + pct(0.99) + ' max=' + pct(1));
const cs = S.player_states.stats(); print('collStats: count=' + cs.count + ' avgObjSize=' + cs.avgObjSize + ' size=' + cs.size + ' storageSize=' + cs.storageSize);
print('record types: ' + Object.entries(recStats).sort((a,b) => b[1].bytes - a[1].bytes).map(([k, v]) => k + ' n=' + v.n + ' avg=' + Math.round(v.bytes/v.n) + ' max=' + v.max).join('; '));
print('largest 5: ' + JSON.stringify(rows.slice().sort((a,b) => b.size - a.size).slice(0, 5).map(r => ({uid: short(r.u), t: r.t, scene: r.scene, size: r.size, nrec: r.nrec, biggestKey: r.biggestKey, biggest: r.biggest}))));
const byUser = {}; for (const r of rows) (byUser[r.u] = byUser[r.u] || []).push(r);
const users = Object.keys(byUser);
let nClose = 0, nCloseUsers = 0, nCloseIdentical = 0, regressions = [], unitBack = [], multi = 0, perCount = {}; const closeExamples = [];
for (const u of users) {
  const seq = byUser[u]; perCount[seq.length] = (perCount[seq.length]||0) + 1; if (seq.length > 1) multi++;
  let userClose = 0;
  for (let i = 1; i < seq.length; i++) {
    const a = seq[i-1], b = seq[i]; const gap = b.t - a.t;
    if (gap < 2000) { nClose++; userClose++; const same = a.scene === b.scene && a.cp === b.cp && a.size === b.size && a.dlg === b.dlg; if (same) nCloseIdentical++; if (closeExamples.length < 10) closeExamples.push({uid: short(u), t: b.t, gapMs: gap, scene: b.scene, cpA: a.cp, cpB: b.cp, sizeA: a.size, sizeB: b.size, identical: same}); }
    const ua = unitNo(a.scene), ub = unitNo(b.scene);
    if (ub !== -1 && ua !== -1 && ub < ua) unitBack.push({uid: short(u), from: a.scene, to: b.scene, at: b.t, gapMin: Math.round(gap/60000)});
    if (a.scene === b.scene && typeof a.cp === 'number' && typeof b.cp === 'number' && b.cp < a.cp) regressions.push({uid: short(u), scene: a.scene, cpFrom: a.cp, cpTo: b.cp, at: b.t, gapMin: Math.round(gap/60000), laterCps: seq.slice(i+1).map(x => x.cp).join(',')});
  }
  if (userClose) nCloseUsers++;
}
print('students with saves: ' + users.length + '; saves per student: ' + JSON.stringify(perCount) + '; students with >1 save: ' + multi);
print('close pairs (<2 s apart): ' + nClose + ' in ' + nCloseUsers + ' students; identical content: ' + nCloseIdentical + '; examples: ' + JSON.stringify(closeExamples));
const gaps = []; for (const u of users) { const s = byUser[u]; for (let i = 1; i < s.length; i++) gaps.push(s[i].t - s[i-1].t); } gaps.sort((a,b) => a-b);
const gp = p => gaps.length ? Math.round(gaps[Math.min(gaps.length-1, Math.floor(p*gaps.length))]/1000) : -1;
print('gap between consecutive saves (s): n=' + gaps.length + ' p10=' + gp(0.1) + ' p50=' + gp(0.5) + ' p90=' + gp(0.9) + '; under 2 s: ' + gaps.filter(g => g < 2000).length + '; under 10 s: ' + gaps.filter(g => g < 10000).length + '; under 60 s: ' + gaps.filter(g => g < 60000).length);
print('checkpoint regressions in the same scene (later save with a lower checkpoint): ' + regressions.length + ' in ' + new Set(regressions.map(r => r.uid)).size + ' students');
for (const r of regressions.slice(0, 80)) print('  ' + JSON.stringify(r));
print('unit went backwards between consecutive saves: ' + unitBack.length);
for (const r of unitBack.slice(0, 40)) print('  ' + JSON.stringify(r));
const since = Date.now() - 28*24*3600*1000; const byDay = {};
for (const r of rows) { if (r.t >= since) { const d = new Date(r.t.getTime() - 5*3600*1000).toISOString().slice(0,10); (byDay[d] = byDay[d] || {n: 0, u: new Set()}); byDay[d].n++; byDay[d].u.add(r.u); } }
print('saves still present per day (Central), last 28 days: ' + Object.keys(byDay).sort().map(d => d + ':' + byDay[d].n + '/' + byDay[d].u.size).join(' '));
const newestScene = {}; for (const u of users) { const s = byUser[u][byUser[u].length-1].scene; newestScene[s] = (newestScene[s]||0) + 1; }
print('newest save scene per student: ' + JSON.stringify(newestScene));
const cpDist = {}; for (const u of users) { const c = byUser[u][byUser[u].length-1].cp; cpDist[c] = (cpDist[c]||0) + 1; }
print('newest save checkpoint value distribution: ' + JSON.stringify(cpDist));
const firstCp = {}; for (const r of rows) { const k = r.scene + ' cp=' + r.cp; if (r.cp === -1 || r.cp === 0) firstCp[k] = (firstCp[k]||0) + 1; }
print('saves at cp -1/0 by scene (unit-start saves): ' + JSON.stringify(firstCp));
