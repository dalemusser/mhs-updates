// Read-only. For each runtime_error crash since 2026-09-14 (stratalog): the student's last save before it and
// the first save after it (scene, checkpoint), and the game's first events after the relaunch. Ids shortened.
const S = db.getSiblingDB('stratasave'), L = db.getSiblingDB('stratalog');
const short = s => '…' + String(s||'').slice(-6);
const cpOf = d => { const l = ((d||{}).save_data||{}).m_list || []; for (const r of l) if (/CheckpointSaver$/.test(r.key||'')) { try { return JSON.parse(r.data).id; } catch (e) { return '?'; } } return null; };
const ev = L.logdata.find({game: 'mhs', eventType: 'crash', serverTimestamp: {$gte: new Date('2026-09-14T05:00:00Z')}, 'data.type': 'runtime_error'}, {user_id: 1, serverTimestamp: 1, version: 1, 'data.unitId': 1}).sort({serverTimestamp: 1}).toArray();
print('runtime_error crashes: ' + ev.length);
for (const x of ev) {
  const uid = String(x.user_id||''); const t = x.serverTimestamp;
  const before = S.player_states.find({game: 'mhs', user_id: uid, timestamp: {$lt: t}}, {timestamp: 1, 'save_data.m_sceneName': 1, 'save_data.m_list': 1}).sort({timestamp: -1}).limit(1).toArray()[0];
  const after = S.player_states.find({game: 'mhs', user_id: uid, timestamp: {$gt: t}}, {timestamp: 1, 'save_data.m_sceneName': 1, 'save_data.m_list': 1}).sort({timestamp: 1}).limit(2).toArray();
  const nextEv = L.logdata.find({game: 'mhs', user_id: uid, serverTimestamp: {$gt: t}, eventType: {$ne: 'crash'}}, {eventType: 1, serverTimestamp: 1, sceneName: 1, 'data.questName': 1, 'data.questEventType': 1}).sort({serverTimestamp: 1}).limit(6).toArray();
  print('\ncrash ' + t.toISOString() + ' ' + short(uid) + ' ' + ((x.data||{}).unitId||'?') + ' v' + (x.version||'?'));
  print('  last save before: ' + (before ? before.timestamp.toISOString() + ' ' + before.save_data.m_sceneName + ' cp=' + cpOf(before) + ' (' + Math.round((t - before.timestamp)/60000) + ' min before)' : 'none (retention or never saved)'));
  for (const a of after) print('  save after:       ' + a.timestamp.toISOString() + ' ' + a.save_data.m_sceneName + ' cp=' + cpOf(a) + ' (+' + Math.round((a.timestamp - t)/60000) + ' min)');
  print('  first events after: ' + nextEv.map(e => Math.round((e.serverTimestamp - t)/1000) + 's ' + e.eventType + (e.data && e.data.questName ? '[' + e.data.questEventType + ' ' + e.data.questName + ']' : '')).join('; '));
}
