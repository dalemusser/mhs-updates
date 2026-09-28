# A line-by-line port of the drop-in GameLogger.cs send loop (SendQueuedLogs, lines 345-437),
# run against a fake log server. toSend is either a new list each pass (the drop-in as written)
# or a field that carries over between passes (as in builds 20260921-12438 and 20260925-12446).
# Run: python3 send-loop-simulation.py
import json
def server(body_entries):
    if len(body_entries) > 100: return 400, 'Batch size exceeds maximum of 100'
    for i, e in enumerate(body_entries):
        if not e.get('user_id'): return 400, f'entry {i}: missing or invalid user_id'
    return 201, 'ok'

def run(queue, hoisted, passes=12, network_up=lambda p: True):
    batchSize, single = 50, 0
    field_toSend = []            # the shipped build's GameLogger.toSend
    sent, log = [], []
    for p in range(passes):
        toSend = field_toSend if hoisted else []
        take = 1 if single > 0 else batchSize
        for entry in queue:
            toSend.append(entry)
            if len(toSend) >= take: break
        if not toSend: log.append(f'pass {p}: queue empty, loop exits'); break
        body = [dict(e, device='…') for e in toSend]
        if not network_up(p):
            log.append(f'pass {p}: network failure, {len(toSend)} entries kept, wait'); continue
        code, msg = server(body)
        if code == 201:
            sent.extend(e['id'] for e in toSend)
            del queue[:len(toSend)]     # RemoveFromQueue(toSend.Count)
            if single > 0: single -= 1
            log.append(f'pass {p}: 201, {len(toSend)} entries {[e["id"] for e in toSend][:6]}, queue now {len(queue)}')
            continue
        if len(toSend) == 1:
            del queue[:1]; single = max(0, single - 1); log.append(f'pass {p}: 400 single, dropped'); continue
        single = len(toSend); log.append(f'pass {p}: 400 on batch of {len(toSend)} ({msg}), switch to single sends'); continue
    return sent, log

ev = lambda i: {'id': f'e{i}', 'user_id': 'u'}
for hoisted in (False, True):
    print(f'\n==== toSend {"kept as a class field (shipped)" if hoisted else "new each pass (drop-in as written)"} ====')
    print('-- normal play: the two opening events queued together')
    sent, log = run([ev(1), ev(2)], hoisted, passes=6); print('\n'.join(log)); print('accepted:', sent)
    print('-- wedged store from v2.8.1: {} first, then 37 events')
    q = [{'id': '{}'}] + [ev(i) for i in range(1, 38)]
    sent, log = run(q, hoisted, passes=70 if hoisted else 45)
    print('\n'.join(log[:4] + (['…'] + log[-3:] if len(log) > 8 else log[4:]))); print('accepted count:', len(sent), 'distinct:', len(set(sent)))
