import sys, base64, json
from collections import Counter
b = open(sys.argv[1]).read().strip()
if b in ('none', 'err', ''):
    print('store:', b or 'empty'); sys.exit()
raw = base64.b64decode(b); open(sys.argv[2], 'wb').write(raw)
txt = raw.decode('utf-8', 'replace')
dec = json.JSONDecoder()
arr = None; fmt = None
j = txt.find('{"logs":')
if j >= 0:
    fmt = 'v1 {"logs":[strings]}'; arr = [json.loads(x) for x in dec.raw_decode(txt[j:])[0]['logs']]
else:
    k = txt.find('game_logs_cache.json')
    if k >= 0:
        a = txt.find('[', k); fmt = 'v2 [objects]'; arr = dec.raw_decode(txt[a:])[0]
if arr is None:
    print(json.dumps({'bytes': len(raw), 'cache': 'absent'})); sys.exit()
kinds = Counter('empty' if not e else 'device-only' if list(e) == ['device'] else 'null-user' if e.get('user_id') is None else 'event' for e in arr)
head = arr[0] if arr else None
print(json.dumps({'bytes': len(raw), 'fmt': fmt, 'n': len(arr), 'kinds': kinds, 'head': ('{}' if head == {} else '+'.join(head)) if head is not None else None,
                  'versions': Counter(e.get('version') for e in arr if e), 'last': (arr[-1].get('eventType'), arr[-1].get('timestamp')) if arr else None}))
