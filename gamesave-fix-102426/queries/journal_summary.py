import json, sys, re, collections
path = sys.argv[1]
by = collections.Counter(); restricted = collections.Counter(); restricted_days = collections.Counter(); errs = []; rejected_days = collections.Counter(); cleanup = collections.Counter()
first = last = None
short = lambda s: re.sub(r'[0-9a-f]{24}', lambda m: '…' + m.group(0)[-6:], str(s))
for line in open(path, encoding='utf-8', errors='replace'):
    line = line.strip()
    if not line.startswith('{'):
        continue
    try:
        d = json.loads(line)
    except Exception:
        continue
    ts = d.get('ts') or d.get('time') or d.get('@timestamp') or ''
    first = first or ts; last = ts
    lvl = d.get('level', '?'); msg = d.get('msg', '?')
    by[(lvl, msg)] += 1
    day = str(ts)[:10]
    if 'restricted API key' in msg:
        ids = d.get('user_ids') or []
        restricted[(d.get('path'), tuple(short(i) for i in ids), d.get('refused'))] += 1
        restricted_days[day] += 1
    if msg.startswith('API request rejected'):
        rejected_days[(day, msg, d.get('path'))] += 1
    if lvl in ('error', 'warn') and ('failed to' in msg or 'cleanup' in msg):
        errs.append((ts, lvl, msg, short(d.get('error', '')), short(d.get('user_id', '')), d.get('game', '')))
    if msg.startswith('cleanup'):
        cleanup[(msg, d.get('deleted'))] += 1
print('window:', first, '..', last)
print('\n== level/msg counts ==')
for (lvl, msg), n in by.most_common(60):
    print(f'{n:7d}  {lvl:6s} {msg}')
print('\n== restricted-key use outside its user ids (path, ids, refused) ==')
for k, n in restricted.most_common(40):
    print(n, k)
print('by day:', dict(sorted(restricted_days.items())))
print('\n== rejected requests by day/msg/path ==')
for k, n in sorted(rejected_days.items()):
    print(n, k)
print('\n== failed/cleanup warn+error lines (first 60) ==')
for e in errs[:60]:
    print(e)
print('\n== cleanup counts ==', dict(cleanup))
