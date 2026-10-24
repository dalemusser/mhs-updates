// Read-only. For the two recent "started over" candidates: the player position stored in the last save before the
// relaunch vs the first PlayerPositionEvents after it, and the dialogue conversations seen right after the relaunch.
const S = db.getSiblingDB('stratasave'), L = db.getSiblingDB('stratalog');
const short = s => '…' + String(s||'').slice(-6);
const cases = [{tail: 'c7f22a', relaunch: '2026-10-07T01:27:17Z'}, {tail: '8b8703', relaunch: '2026-10-15T14:55:57Z'}, {tail: '8b8703', relaunch: '2026-10-15T14:38:18Z'}];
for (const c of cases) {
  const u = S.player_states.findOne({game: 'mhs', user_id: {$regex: c.tail + '$'}}, {user_id: 1}); const uid = u.user_id; const t = new Date(c.relaunch);
  const before = S.player_states.find({game: 'mhs', user_id: uid, timestamp: {$lt: t}}).sort({timestamp: -1}).limit(1).toArray()[0];
  const posRec = before ? (before.save_data.m_list||[]).find(r => /PositionSaver$/.test(r.key||'')) : null;
  print('\n### ' + short(uid) + ' relaunch ' + c.relaunch + '; last save before: ' + (before ? before.timestamp.toISOString() + ' ' + before.save_data.m_sceneName : 'none'));
  print('  saved position record: ' + (posRec ? posRec.key + ' ' + String(posRec.data).slice(0, 200) : 'none'));
  const evs = L.logdata.find({game: 'mhs', user_id: uid, serverTimestamp: {$gte: t, $lte: new Date(t.getTime() + 6*60000)}}, {eventType: 1, serverTimestamp: 1, timestamp: 1, sceneName: 1, data: 1}).sort({serverTimestamp: 1}).limit(400).toArray();
  const pos = evs.filter(e => e.eventType === 'PlayerPositionEvent').slice(0, 4);
  print('  first positions after relaunch: ' + pos.map(e => e.serverTimestamp.toISOString().slice(11,19) + ' ' + JSON.stringify((e.data||{}).position)).join(' | '));
  const dlg = evs.filter(e => /Dialogue/.test(e.eventType)).slice(0, 12);
  print('  first dialogue events: ' + dlg.map(e => e.serverTimestamp.toISOString().slice(11,19) + ' ' + e.eventType + ' ' + JSON.stringify(e.data||{}).slice(0, 80)).join(' | '));
  const types = {}; for (const e of evs) types[e.eventType] = (types[e.eventType]||0) + 1; print('  event types in 6 min: ' + JSON.stringify(types));
}
