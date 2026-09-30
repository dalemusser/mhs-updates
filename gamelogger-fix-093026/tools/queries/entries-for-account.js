// Entries for one account in a window. Set the three values below.
const uid = 'ACCOUNT_USER_ID_HEX';                     // 24-hex user id
const from = new Date('2026-09-30T00:00:00Z'), to = new Date('2026-10-01T00:00:00Z');
const L = db.getSiblingDB('stratalog');
const docs = L.logdata.find({game: 'mhs', user_id: uid, serverTimestamp: {$gte: from, $lt: to}}, {eventType: 1, timestamp: 1, serverTimestamp: 1, version: 1, data: 1, eventKey: 1}).sort({_id: 1}).toArray();
const key = d => d.eventType + '|' + d.timestamp + '|' + JSON.stringify(d.data);
const c = {}; docs.forEach(d => c[key(d)] = (c[key(d)] || 0) + 1);
const vals = Object.values(c);
print('entries', docs.length, '| distinct events', vals.length, '| max copies of one event', vals.length ? Math.max(...vals) : 0, '| events with >1 copy', vals.filter(v => v > 1).length);
const byType = {}; docs.forEach(d => byType[d.eventType] = (byType[d.eventType] || 0) + 1); print('by type', JSON.stringify(byType));
const byVersion = {}; docs.forEach(d => byVersion[d.version] = (byVersion[d.version] || 0) + 1); print('by version', JSON.stringify(byVersion));
L.logdata.aggregate([{$match: {game: 'mhs', user_id: uid, serverTimestamp: {$gte: from, $lt: to}}}, {$group: {_id: {$dateTrunc: {date: '$serverTimestamp', unit: 'second', binSize: 10}}, n: {$sum: 1}}}, {$sort: {_id: 1}}]).forEach(b => print('  ', b._id.toISOString().slice(11, 19), b.n));
