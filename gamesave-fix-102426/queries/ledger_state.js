// Read-only. The save service's ledger (refused and failed requests only) for the state and settings APIs:
// all time by path/status/message, the last 28 days by day, the 5xx details, the 4xx bodies (ids shortened).
const S = db.getSiblingDB('stratasave');
const since = new Date(Date.now() - 28*24*3600*1000);
const short = s => String(s||'').replace(/[0-9a-f]{24}/g, m => '…' + m.slice(-6));
const paths = ['/api/state/save', '/api/state/load', '/api/settings/save', '/api/settings/load', '/save', '/load'];
print('=== all time: path / status / error_message / n / first / last ===');
S.ledger_entries.aggregate([{$match: {path: {$in: paths}}}, {$group: {_id: {p: '$path', s: '$status_code', e: '$error_message'}, n: {$sum: 1}, first: {$min: '$started_at'}, last: {$max: '$started_at'}}}, {$sort: {'_id.p': 1, '_id.s': 1, n: -1}}]).forEach(a => print([a._id.p, a._id.s, short(a._id.e), a.n, a.first.toISOString().slice(0,10), a.last.toISOString().slice(0,10)].join(' | ')));
print('\n=== last 28 days: day / path / status / n ===');
S.ledger_entries.aggregate([{$match: {path: {$in: paths}, started_at: {$gte: since}}}, {$group: {_id: {d: {$dateToString: {format: '%Y-%m-%d', date: '$started_at'}}, p: '$path', s: '$status_code'}, n: {$sum: 1}}}, {$sort: {'_id.d': 1, '_id.p': 1}}]).forEach(a => print([a._id.d, a._id.p, a._id.s, a.n].join(' | ')));
print('\n=== 5xx on state paths, all time: time / path / message / total_ms / body_size / ua ===');
S.ledger_entries.find({path: {$in: ['/api/state/save', '/api/state/load']}, status_code: {$gte: 500}}).sort({started_at: 1}).forEach(e => print([e.started_at.toISOString(), e.path, short(e.error_message).slice(0,160), Math.round((e.timing||{}).total_ms||0), e.request_body_size, ((e.headers||{})['User-Agent']||'').slice(0,60)].join(' | ')));
print('\n=== 4xx on state paths, last 90 days: time / path / status / message / body preview (shortened) / ua ===');
S.ledger_entries.find({path: {$in: ['/api/state/save', '/api/state/load']}, status_code: {$gte: 400, $lt: 500}, started_at: {$gte: new Date(Date.now() - 90*24*3600*1000)}}).sort({started_at: 1}).forEach(e => print([e.started_at.toISOString(), e.path, e.status_code, short(e.error_message), short(e.request_body_preview).slice(0,120), ((e.headers||{})['User-Agent']||'').slice(0,50)].join(' | ')));
print('\n=== state 400s by month and message (all time) ===');
S.ledger_entries.aggregate([{$match: {path: {$in: ['/api/state/save', '/api/state/load']}, status_code: 400}}, {$group: {_id: {m: {$dateToString: {format: '%Y-%m', date: '$started_at'}}, p: '$path', e: '$error_message'}, n: {$sum: 1}, minB: {$min: '$request_body_size'}, maxB: {$max: '$request_body_size'}}}, {$sort: {'_id.m': 1, '_id.p': 1}}]).forEach(a => print([a._id.m, a._id.p, short(a._id.e), a.n, a.minB + '..' + a.maxB + ' B'].join(' | ')));
print('\n=== api_stats / daily_stats shape ===');
for (const c of ['api_stats', 'daily_stats']) { const d = S.getCollection(c).find().sort({_id: -1}).limit(1).toArray()[0]; print(c + ': ' + (d ? JSON.stringify(d).slice(0, 600) : 'empty')); print(c + ' count: ' + S.getCollection(c).estimatedDocumentCount()); }
