// Paste the JSON array printed by: python3 - <<'PY' ... (see parse_store.py; or build
// [[eventType, timestamp, user_id], ...] from the exported queue) into `backlog`.
const backlog = [];                                     // e.g. [["PlayerPositionEvent","2026-09-28T09:24:34.0940000Z","<user id>"], ...]
const uid = 'ACCOUNT_USER_ID_HEX';
const since = new Date('2026-09-30T00:00:00Z');
const L = db.getSiblingDB('stratalog');
const ms = v => new Date(String(v).slice(0, 23).replace(/(\.\d{3})\d*Z?$/, '$1Z')).getTime();
const docs = L.logdata.find({game: 'mhs', user_id: uid, serverTimestamp: {$gte: since}}, {eventType: 1, timestamp: 1}).toArray();
const c = {}; docs.forEach(d => { const k = d.eventType + '|' + ms(d.timestamp); c[k] = (c[k] || 0) + 1; });
const withId = backlog.filter(b => b[2]);
const once = withId.filter(([t, ts]) => c[t + '|' + ms(ts)] === 1).length;
const never = withId.filter(([t, ts]) => !c[t + '|' + ms(ts)]).map(([t, ts]) => t + ' ' + ts);
print('queued events with a user id', withId.length, '| arrived exactly once', once, '| never arrived', never.length, JSON.stringify(never.slice(0, 10)));
