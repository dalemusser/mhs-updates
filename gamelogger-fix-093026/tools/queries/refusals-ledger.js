// Refused log requests in a window, from the log service's ledger.
const from = new Date('2026-09-30T00:00:00Z'), to = new Date('2026-10-01T00:00:00Z');
const L = db.getSiblingDB('stratalog');
const q = {started_at: {$gte: from, $lt: to}, path: '/api/log/submit'};
print('refused requests:', L.ledger_entries.countDocuments(q));
L.ledger_entries.aggregate([{$match: q}, {$group: {_id: {s: '$status_code', e: '$error_message'}, n: {$sum: 1}, first: {$min: '$started_at'}, last: {$max: '$started_at'}, minSize: {$min: '$request_body_size'}, maxSize: {$max: '$request_body_size'}}}, {$sort: {first: 1}}]).forEach(a => print(JSON.stringify(a)));
const all = L.ledger_entries.find(q, {started_at: 1, request_body_preview: 1}).sort({started_at: 1}).toArray();
if (all.length > 1) { const gaps = []; for (let i = 1; i < all.length; i++) gaps.push(all[i].started_at - all[i - 1].started_at); gaps.sort((a, b) => a - b); print('interval ms: min', gaps[0], 'median', gaps[Math.floor(gaps.length / 2)]); }
if (all.length) print('first body preview:', all[0].request_body_preview.slice(0, 300));
