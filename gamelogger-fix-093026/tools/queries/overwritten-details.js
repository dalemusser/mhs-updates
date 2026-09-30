// Share of entries whose details disagree with their own key or timestamp, by lateness.
// Set the version string of the build under test (the game's Application.version).
const version = 'VERSION_STRING';                        // e.g. '20260914-' for the schools' build
const from = new Date('2026-09-14T00:00:00Z');
const L = db.getSiblingDB('stratalog');
const base = {game: 'mhs', serverTimestamp: {$gte: from}, version: version, timestamp: {$regex: '^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}'}};
// DocumentDB: $dateFromString without onError; the regex above keeps it safe. Seconds precision.
const delay = {$let: {vars: {ts: {$dateFromString: {dateString: {$concat: [{$substrCP: ['$timestamp', 0, 19]}, 'Z']}}}},
  in: {$switch: {branches: [
    {case: {$lt: [{$subtract: ['$serverTimestamp', '$$ts']}, 6000]}, then: 'a: sent within ~5 s'},
    {case: {$lt: [{$subtract: ['$serverTimestamp', '$$ts']}, 120000]}, then: 'b: 5 s - 2 min late'}], default: 'c: over 2 min late (backlog)'}}}};
function run(label, match, mismatch) {
  const rows = L.logdata.aggregate([{$match: base}, {$match: match}, {$project: {bad: mismatch, d: delay, u: '$user_id'}},
    {$group: {_id: '$d', n: {$sum: 1}, bad: {$sum: {$cond: ['$bad', 1, 0]}}, users: {$addToSet: {$cond: ['$bad', '$u', null]}}}}, {$sort: {_id: 1}}], {allowDiskUse: true}).toArray();
  print('== ' + label);
  rows.forEach(r => print('   ', r._id, '| entries', r.n, '| details disagree', r.bad, '(' + (r.n ? (100 * r.bad / r.n).toFixed(2) : 0) + '%)', '| accounts', r.users.filter(x => x).length));
}
run('dialogue-node events (eventKey DialogueNodeEvent:conversation:node vs data)',
  {eventType: 'DialogueEvent', eventKey: {$regex: '^DialogueNodeEvent:'}},
  {$or: [{$ne: [{$arrayElemAt: [{$split: ['$eventKey', ':']}, 1]}, {$toString: '$data.conversationId'}]},
         {$ne: [{$arrayElemAt: [{$split: ['$eventKey', ':']}, 2]}, {$toString: '$data.nodeId'}]},
         {$ne: ['$data.dialogueEventType', 'DialogueNodeEvent']}]});
run('questEvent (eventKey questEventType:questID vs data)', {eventType: 'questEvent'},
  {$or: [{$ne: [{$arrayElemAt: [{$split: ['$eventKey', ':']}, 0]}, '$data.questEventType']},
         {$ne: [{$arrayElemAt: [{$split: ['$eventKey', ':']}, 1]}, {$toString: '$data.questID'}]}]});
run('PuzzlePieceVisibleEvent (data.timestamp vs the entry timestamp)', {eventType: 'PuzzlePieceVisibleEvent'}, {$ne: ['$data.timestamp', '$timestamp']});
