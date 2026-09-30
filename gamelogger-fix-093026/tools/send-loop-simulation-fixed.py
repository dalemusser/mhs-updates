# A line-by-line port of the FIXED GameLogger.cs (Game-Code/Systems/Logging/GameLogger.cs,
# 2026-09-30) send loop, cache load and SetUserId, run against a fake log server. It checks
# the behaviours that the browser tests then confirm on a real build:
#   defect 2  every accepted batch goes out once; a refused batch never loops
#   defect 4  an entry logged before the user id is known waits, is stamped, and is sent
#   defect 6  an empty or id-less cached entry is dropped without a request; a refusal is
#             followed by a pause; sent entries are removed by identity
#   defect 3  (copy on enqueue) a reused caller dictionary cannot change a queued entry
# Run: python3 send-loop-simulation-fixed.py   (exits non-zero on any failed check)
import copy, sys

MAX_BATCH = 50

def server(body_entries):
    if len(body_entries) > 100: return 400, 'Batch size exceeds maximum of 100'
    for i, e in enumerate(body_entries):
        if not isinstance(e.get('user_id'), str) or not e.get('user_id'): return 400, f'entry {i}: missing or invalid user_id'
        if not e.get('eventType'): return 400, f'entry {i}: missing eventType'
    return 201, 'ok'

def sendable(e):
    return bool(e) and isinstance(e.get('eventType'), str) and e['eventType'] != '' and isinstance(e.get('user_id'), str) and e['user_id'] != ''

def lacks_only_user_id(e):
    return bool(e) and isinstance(e.get('eventType'), str) and e['eventType'] != '' and not (isinstance(e.get('user_id'), str) and e['user_id'] != '')

class Logger:
    def __init__(self, cached=None):
        self.user_id = None
        self.queue = []
        self.batch_size = MAX_BATCH
        self.single_remaining = 0
        self.requests = []          # (entries sent, code)
        self.waits = []             # ('identity' | 'rejected' | 'backoff', seconds)
        self.dropped_on_load = 0
        if cached is not None:
            for e in cached:        # LoadEntries
                if sendable(e): self.queue.append(e)
                else: self.dropped_on_load += 1

    def log_event(self, event_type, data):   # LogEvent: the entry owns a copy of data
        self.queue.append({'game': 'mhs', 'user_id': self.user_id, 'eventType': event_type, 'timestamp': f't{len(self.requests)}', 'data': copy.deepcopy(data)})

    def set_user_id(self, uid):
        self.user_id = uid
        for e in self.queue:
            if e and not (isinstance(e.get('user_id'), str) and e['user_id']): e['user_id'] = uid

    def send_pass(self, network_up=True):
        """One pass of SendQueuedLogs. Returns 'exit', 'wait', or 'sent'."""
        to_send = []                                   # new list every pass
        wait_identity = False
        while self.queue and not sendable(self.queue[0]):
            if self.user_id is None and lacks_only_user_id(self.queue[0]):
                wait_identity = True; break
            self.queue.pop(0)                          # dropped, no request
        if not wait_identity:
            take = 1 if self.single_remaining > 0 else self.batch_size
            for e in self.queue:
                if not sendable(e): break
                to_send.append(e)
                if len(to_send) >= take: break
        if wait_identity:
            self.waits.append(('identity', 1)); return 'wait'
        if not to_send: return 'exit'
        if not network_up:
            self.waits.append(('backoff', 10)); return 'wait'
        body = [dict(e, device='...') for e in to_send]
        code, msg = server(body)
        self.requests.append(([e['eventType'] + '/' + e['timestamp'] for e in to_send], code))
        if code == 201:
            self.remove_sent(to_send)
            if self.single_remaining > 0: self.single_remaining -= 1
            return 'sent'
        if code == 400:
            if len(to_send) == 1:
                self.remove_sent(to_send)
                if self.single_remaining > 0: self.single_remaining -= 1
            else:
                self.single_remaining = len(to_send)
            self.waits.append(('rejected', 1)); return 'sent'
        raise AssertionError(code)

    def remove_sent(self, sent):                       # by identity
        ids = {id(e) for e in sent}
        while self.queue and id(self.queue[0]) in ids: self.queue.pop(0)

    def run(self, max_passes=200, network_up=lambda p: True, on_pass=None):
        for p in range(max_passes):
            if on_pass: on_pass(self, p)
            r = self.send_pass(network_up(p))
            if r == 'exit': return p
        raise AssertionError('loop did not exit')

failures = 0
def check(name, cond, detail=''):
    global failures
    print(('PASS ' if cond else 'FAIL ') + name + (': ' + str(detail) if detail else ''))
    if not cond: failures += 1

ev = lambda i, uid='u': {'game': 'mhs', 'user_id': uid, 'eventType': f'e{i}', 'timestamp': f's{i}', 'data': {}}

# Defect 2: normal play, the two opening events queued together, then more.
L = Logger(); L.user_id = 'u'
L.log_event('gameWindowFocusEvent', {}); L.log_event('gameStartEvent', {})
L.run()
accepted = [r for r in L.requests if r[1] == 201]
check('defect 2: two opening events go out in one accepted request and the loop exits', len(L.requests) == 1 and accepted[0][0] == ['gameWindowFocusEvent/t0', 'gameStartEvent/t0'] and not L.queue, L.requests)

# Defect 2: a 120-entry backlog drains in 3 requests (50+50+20), every event once.
L = Logger([ev(i) for i in range(120)]); L.user_id = 'u'
L.run()
sent = [x for r in L.requests for x in r[0]]
check('defect 2: 120-entry backlog = 3 requests, each event exactly once', len(L.requests) == 3 and len(sent) == 120 and len(set(sent)) == 120, [len(r[0]) for r in L.requests])

# Defect 6: the store wedged by v2.8.1: {} first, then 37 events, one of them with a null id.
wedged = [{}] + [ev(i) for i in range(1, 20)] + [ev(20, None)] + [ev(i) for i in range(21, 38)]
L = Logger(wedged); L.user_id = 'u'
L.run()
check('defect 6: {} and the null-id entry dropped on load, nothing refused', L.dropped_on_load == 2 and all(r[1] == 201 for r in L.requests) and len(L.requests) == 1 and len(L.requests[0][0]) == 36, (L.dropped_on_load, [(len(r[0]), r[1]) for r in L.requests]))

# Defect 6: an id-less entry that gets past the load filter (queued this session with the id
# already known — cannot happen, but the send-time guard covers it): dropped without a request.
L = Logger([ev(1), ev(2)]); L.user_id = 'u'
L.queue.insert(1, {'game': 'mhs', 'user_id': '', 'eventType': 'x', 'timestamp': 'sx', 'data': {}})
L.run()
check('defect 6: an unsendable entry in the middle is dropped, the rest sent, no refusal', all(r[1] == 201 for r in L.requests) and [x for r in L.requests for x in r[0]] == ['e1/s1', 'e2/s2'], L.requests)

# Defect 6: a batch the server refuses (an entry it dislikes for another reason): the batch is
# resent one at a time, the bad one dropped once, the rest accepted, with a pause after each refusal.
_server = server
class PickyServer:
    def __call__(self, body):
        for i, e in enumerate(body):
            if e['eventType'] == 'bad': return 400, f'entry {i}: invalid'
        return _server(body)
server = PickyServer()
L = Logger([ev(1), dict(ev(2), eventType='bad'), ev(3)]); L.user_id = 'u'
L.run()
codes = [r[1] for r in L.requests]
check('defect 6: refused batch → single sends, one 400 for the bad entry, others accepted, then exit', codes == [400, 201, 400, 201] and not L.queue, codes)
check('defect 6: a pause follows every refusal', sum(1 for w in L.waits if w[0] == 'rejected') == 2, L.waits)
server = _server

# Defect 4: the first event is logged before SetUserId. It waits (no request), is stamped, then sent.
L = Logger([])
L.log_event('DEBUGMenu', {'isOpened': False})
r1 = L.send_pass(); r2 = L.send_pass()
check('defect 4: an id-less entry waits for identity without a request', r1 == 'wait' and r2 == 'wait' and L.requests == [] and L.waits[-1][0] == 'identity', (r1, r2, L.requests))
L.set_user_id('abcdef0123456789abcdef01')
L.log_event('gameStartEvent', {})
L.run()
check('defect 4: after SetUserId the stamped entry and the next event go out together, accepted', len(L.requests) == 1 and L.requests[0][1] == 201 and L.requests[0][0][0].startswith('DEBUGMenu'), L.requests)

# Defect 4 + cache: a cached backlog with ids is sent while the session's own first event still waits.
L = Logger([ev(1), ev(2)])
L.log_event('DEBUGMenu', {'isOpened': False})       # user id unknown
r = L.send_pass()
check('defect 4: a cached backlog goes out before identity; the id-less head then waits', r == 'sent' and L.requests[0][1] == 201 and len(L.requests[0][0]) == 2 and L.send_pass() == 'wait', L.requests)

# Defect 3: copy on enqueue. One component reuses its dictionary for two events in one frame.
L = Logger([]); L.user_id = 'u'
shared = {'conversationId': 16, 'nodeId': 3}
L.log_event('DialogueEvent', shared)
shared.clear(); shared.update({'conversationId': 18, 'nodeId': 1})
L.log_event('DialogueEvent', shared)
check('defect 3: the first queued entry keeps its own details', L.queue[0]['data'] == {'conversationId': 16, 'nodeId': 3} and L.queue[1]['data'] == {'conversationId': 18, 'nodeId': 1}, [e['data'] for e in L.queue])

# Retry-forever while the host is unreachable, then the backlog once after it returns.
L = Logger([ev(i) for i in range(5)]); L.user_id = 'u'
L.run(network_up=lambda p: p >= 6)
check('outage: the loop keeps waiting while the host is down and sends the backlog once after', sum(1 for w in L.waits if w[0] == 'backoff') == 6 and len(L.requests) == 1 and L.requests[0][1] == 201 and len(L.requests[0][0]) == 5, (len(L.waits), L.requests))

# Identity removal survives cache trimming during an in-flight send: the head entries vanish
# while the request is out; the response must not remove entries that were never sent.
L = Logger([ev(i) for i in range(4)]); L.user_id = 'u'
to_send = list(L.queue[:2])
L.queue.pop(0)                    # trimming dropped e0 while the request was in flight
L.remove_sent(to_send)
check('identity removal: only the sent entries still at the head are removed', [e['eventType'] for e in L.queue] == ['e2', 'e3'], [e['eventType'] for e in L.queue])

print('\nchecks failed:', failures)
sys.exit(1 if failures else 0)
