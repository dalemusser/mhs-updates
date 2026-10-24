// Read-only. (a) save sizes for saves made since 2026-09-14 (the current builds), by record type;
// (b) the timelines of the recent "unit started over" and "unit went backwards" cases. Ids shortened.
const S = db.getSiblingDB('stratasave'), L = db.getSiblingDB('stratalog'), H = db.getSiblingDB('stratahub');
const short = s => '…' + String(s||'').slice(-6);
const since = new Date('2026-09-14T05:00:00Z');
const sizes = [], recStats = {}; let n = 0, withSettings = 0, withCustom = 0;
S.player_states.find({game: 'mhs', timestamp: {$gte: since}}).batchSize(50).forEach(d => {
  n++; let total = 0, hasS = false, hasC = false;
  for (const r of ((d.save_data||{}).m_list||[])) { const len = String(r.data||'').length; total += len; const k = String(r.key||''); if (/SettingsSaver$/.test(k)) hasS = true; if (/CharacterCustomization_DataController$/.test(k)) hasC = true; const base = k.replace(/^.*?(DialogueSystemSaver|CheckpointSaver|PositionSaver|MultiEnabledSaver|MultiActiveSaver|EnabledSaver|ActiveSaver|DoorLockSaver|PuzzleSaver|CustomWaypointSaver|SettingsSaver|CharacterCustomization_DataController)$/, '$1'); const st = recStats[base] = recStats[base] || {n: 0, bytes: 0, max: 0}; st.n++; st.bytes += len; if (len > st.max) st.max = len; }
  sizes.push(total); if (hasS) withSettings++; if (hasC) withCustom++;
});
sizes.sort((a,b) => a-b); const pct = p => sizes[Math.min(sizes.length-1, Math.floor(p*sizes.length))];
print('saves since 2026-09-14: ' + n + '; bytes min=' + pct(0) + ' p50=' + pct(0.5) + ' p90=' + pct(0.9) + ' p99=' + pct(0.99) + ' max=' + pct(1) + '; with SettingsSaver record: ' + withSettings + '; with CharacterCustomization record: ' + withCustom);
print('record types since 2026-09-14: ' + Object.entries(recStats).sort((a,b) => b[1].bytes - a[1].bytes).map(([k, v]) => k + ' n=' + v.n + ' avg=' + Math.round(v.bytes/v.n) + ' max=' + v.max).join('; '));
const cpOf = d => { for (const r of ((d.save_data||{}).m_list||[])) if (/CheckpointSaver$/.test(r.key||'')) { try { return JSON.parse(r.data).id; } catch (e) { return '?'; } } return null; };
const cases = [{tail: '8b8703', from: '2026-10-15T14:00:00Z', to: '2026-10-15T15:30:00Z'}, {tail: 'c7f22a', from: '2026-10-06T15:00:00Z', to: '2026-10-07T02:30:00Z'}, {tail: 'c7f231', from: '2026-10-06T13:00:00Z', to: '2026-10-06T14:30:00Z'}];
for (const c of cases) {
  const u = S.player_states.findOne({game: 'mhs', user_id: {$regex: c.tail + '$'}}, {user_id: 1}); if (!u) { print('no user ' + c.tail); continue; }
  const uid = u.user_id; print('\n########## ' + short(uid) + ' window ' + c.from + ' .. ' + c.to);
  const prog = H.mhs_user_progress.findOne({user_id: uid}) || H.mhs_user_progress.findOne({user_id: new ObjectId(uid)});
  print('mhs_user_progress: ' + JSON.stringify(prog ? Object.fromEntries(Object.entries(prog).filter(([k]) => !/_id|user/.test(k))) : null).slice(0, 400));
  const saves = S.player_states.find({game: 'mhs', user_id: uid}).sort({timestamp: 1}).toArray();
  print('saves: ' + saves.map(s => s.timestamp.toISOString() + ' ' + s.save_data.m_sceneName + ' cp=' + cpOf(s)).join(' | '));
  const evs = L.logdata.find({game: 'mhs', user_id: uid, serverTimestamp: {$gte: new Date(c.from), $lte: new Date(c.to)}}, {eventType: 1, serverTimestamp: 1, sceneName: 1, version: 1, data: 1}).sort({serverTimestamp: 1}).toArray();
  print('events: ' + evs.length);
  let last = null;
  for (const e of evs) {
    const t = e.eventType; const d = e.data || {};
    const interesting = /gameStart|questEvent|QuestEvent|crash|DEBUGMenu|gameWindowFocus|gameWindowUnfocus|Checkpoint|checkpoint|LogCacheOverflow|SceneLoad|sceneLoad/.test(t) || (d.questEventType);
    if (!interesting) continue;
    const line = e.serverTimestamp.toISOString().slice(11,19) + ' ' + t + ' ' + (e.sceneName||'') + ' v' + (e.version||'') + ' ' + JSON.stringify(d).slice(0, 110);
    if (line.slice(9) !== last) print('  ' + line); last = line.slice(9);
  }
  const audit = H.audit_events.find({$or: [{target_id: uid}, {'details.user_id': uid}, {user_id: uid}], created_at: {$gte: new Date(c.from) - 7*24*3600*1000}}).limit(10).toArray();
  print('audit events naming the user (recent): ' + audit.map(a => JSON.stringify(a).replace(/[0-9a-f]{24}/g, m => '…' + m.slice(-6)).slice(0, 200)).join(' || '));
}
