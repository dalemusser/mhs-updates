// Read-only. The three saves the service failed with "context canceled" in the last 28 days (500 to the game):
// what the student had saved before and after, and the game's events around that minute.
const S = db.getSiblingDB('stratasave'), L = db.getSiblingDB('stratalog');
const short = s => '…' + String(s||'').slice(-6);
const cpOf = d => { const l = ((d||{}).save_data||{}).m_list || []; for (const r of l) if (/CheckpointSaver$/.test(r.key||'')) { try { return JSON.parse(r.data).id; } catch (e) { return '?'; } } return null; };
const fails = S.ledger_entries.find({path: '/api/state/save', status_code: {$gte: 500}, started_at: {$gte: new Date(Date.now() - 60*24*3600*1000)}}).sort({started_at: 1}).toArray();
for (const f of fails) {
  const m = /"user_id"\s*:\s*"([0-9a-f]{24})"/.exec(f.request_body_preview||'') || /"user_id"\s*:\s*"([0-9a-f]{24})"/.exec(f.request_body||''); const uid = m ? m[1] : null;
  print('\nfailed save ' + f.started_at.toISOString() + ' total_ms=' + Math.round((f.timing||{}).total_ms||0) + ' body=' + f.request_body_size + ' B uid=' + (uid ? short(uid) : '?') + ' msg=' + f.error_message + ' ua=' + ((f.headers||{})['User-Agent']||'').slice(0,70));
  if (!uid) continue;
  const t = f.started_at;
  const before = S.player_states.find({game: 'mhs', user_id: uid, timestamp: {$lt: t}}).sort({timestamp: -1}).limit(2).toArray();
  const after = S.player_states.find({game: 'mhs', user_id: uid, timestamp: {$gt: t}}).sort({timestamp: 1}).limit(3).toArray();
  for (const b of before.reverse()) print('  save before: ' + b.timestamp.toISOString() + ' ' + b.save_data.m_sceneName + ' cp=' + cpOf(b) + ' (' + Math.round((t - b.timestamp)/60000) + ' min before)');
  for (const a of after) print('  save after:  ' + a.timestamp.toISOString() + ' ' + a.save_data.m_sceneName + ' cp=' + cpOf(a) + ' (+' + Math.round((a.timestamp - t)/60000) + ' min)');
  const evs = L.logdata.find({game: 'mhs', user_id: uid, serverTimestamp: {$gte: new Date(t.getTime() - 3*60000), $lte: new Date(t.getTime() + 20*60000)}}, {eventType: 1, serverTimestamp: 1, sceneName: 1, 'data.questName': 1, 'data.questEventType': 1, 'data.actionType': 1}).sort({serverTimestamp: 1}).limit(60).toArray();
  print('  events −3..+20 min: ' + evs.map(e => Math.round((e.serverTimestamp - t)/1000) + 's ' + e.eventType + (e.data && e.data.questName ? '[' + e.data.questEventType + ' ' + String(e.data.questName).slice(0,25) + ']' : '')).join('; '));
}
