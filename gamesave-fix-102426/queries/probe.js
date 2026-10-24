// Read-only probe of the save service's database: what exists, how big, index names, document shape (keys only).
const names = db.adminCommand({listDatabases: 1}).databases.map(d => d.name + ':' + Math.round(d.sizeOnDisk/1e6) + 'MB');
print('databases: ' + names.join(', '));
const S = db.getSiblingDB('stratasave');
print('collections: ' + S.getCollectionNames().sort().join(', '));
for (const c of ['player_states', 'player_settings', 'ledger_entries']) {
  print(c + ': ' + S.getCollection(c).estimatedDocumentCount() + ' docs; indexes: ' + S.getCollection(c).getIndexes().map(i => i.name + JSON.stringify(i.key)).join(' '));
}
const newest = S.player_states.find({game: 'mhs'}, {user_id: 1, timestamp: 1, 'save_data.m_sceneName': 1, 'save_data.m_version': 1}).sort({_id: -1}).limit(3).toArray();
print('newest mhs states (id shortened): ' + JSON.stringify(newest.map(d => ({id: d._id.toHexString().slice(-6), uid: String(d.user_id).slice(-6), t: d.timestamp, scene: (d.save_data||{}).m_sceneName, v: (d.save_data||{}).m_version}))));
const one = S.player_states.findOne({game: 'mhs'}, {save_data: 1});
if (one) {
  const sd = one.save_data || {};
  print('save_data top keys: ' + Object.keys(sd).join(', '));
  const list = sd.m_list || [];
  print('m_list records: ' + list.length + '; keys/sceneIndex/dataLen: ' + list.map(r => r.key + '/' + r.sceneIndex + '/' + String(r.data||'').length).join(', '));
  print('approx document bytes: ' + JSON.stringify(one).length);
}
const games = S.player_states.aggregate([{$group: {_id: '$game', n: {$sum: 1}, users: {$addToSet: '$user_id'}}}, {$project: {n: 1, users: {$size: '$users'}}}]).toArray();
print('states by game: ' + JSON.stringify(games));
const led = S.ledger_entries.aggregate([{$group: {_id: {p: '$path', s: '$status_code'}, n: {$sum: 1}, first: {$min: '$started_at'}, last: {$max: '$started_at'}}}, {$sort: {'_id.p': 1, '_id.s': 1}}]).toArray();
print('ledger by path/status (all time): ' + JSON.stringify(led));
