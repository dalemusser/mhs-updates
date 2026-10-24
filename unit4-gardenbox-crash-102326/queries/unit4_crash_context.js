// Read-only. Every runtime_error crash event (stratalog) since 2026-09-14 with the full
// device block, the full stack, and the 40 game events before / 15 after, with their data.
const db2 = db.getSiblingDB('stratahub'), db3 = db.getSiblingDB('stratalog');
const base = new Date('2026-09-14T05:00:00Z');
const userRole = {}; db2.users.find({}, {role:1}).forEach(u => userRole[u._id.toHexString()] = u.role);
const ct = d => d ? new Date(d.getTime() - 5*3600*1000).toISOString().slice(0,19).replace('T',' ') : '-';
const hms = d => d ? new Date(d.getTime() - 5*3600*1000).toISOString().slice(11,23) : '-';
const ev = db3.logdata.find({game:'mhs', eventType:'crash', serverTimestamp:{$gte:base}, 'data.type':'runtime_error'}).sort({serverTimestamp:1}).toArray();
print('runtime_error crash events: ' + ev.length);
let n = 0;
for (const x of ev) {
  n++; const d = x.data||{}, uid = String(x.user_id||'');
  print('\n################ crash ' + n + ' — ' + ct(x.serverTimestamp) + ' CT — student …' + uid.slice(-6) + ' (' + (userRole[uid]||'?') + ') — ' + (d.unitId||'?') + ' v' + (x.version||'?') + ' build ' + (x.buildVersion||x.version||''));
  print('message: ' + d.message); print('type/phase: ' + d.type + ' / ' + d.phase);
  print('device: ' + JSON.stringify(x.device||{}));
  print('data keys: ' + Object.keys(d).join(','));
  print('stack:\n' + String(d.stack||'').split(' at ').join('\n   at '));
  const prev = db3.logdata.find({game:'mhs', user_id: uid, serverTimestamp:{$lt: x.serverTimestamp, $gte: new Date(x.serverTimestamp.getTime() - 30*60*1000)}, eventType:{$ne:'crash'}}).sort({serverTimestamp:-1}).limit(40).toArray().reverse();
  print('--- ' + prev.length + ' events before (server time CT; game timestamp; type; scene; data)');
  for (const e of prev) print('  ' + hms(e.serverTimestamp) + '  ' + String(e.timestamp||'').slice(11,23) + '  ' + (e.eventType||'?').padEnd(26) + ' ' + String(e.sceneName||'').padEnd(26) + ' ' + JSON.stringify(e.data||{}).slice(0,220));
  const after = db3.logdata.find({game:'mhs', user_id: uid, serverTimestamp:{$gt: x.serverTimestamp, $lt: new Date(x.serverTimestamp.getTime() + 30*60*1000)}, eventType:{$ne:'crash'}}).sort({serverTimestamp:1}).limit(15).toArray();
  print('--- ' + after.length + ' events after (first 15)');
  for (const e of after) print('  ' + hms(e.serverTimestamp) + '  ' + String(e.timestamp||'').slice(11,23) + '  ' + (e.eventType||'?').padEnd(26) + ' ' + String(e.sceneName||'').padEnd(26) + ' ' + JSON.stringify(e.data||{}).slice(0,220));
}
// How often the garden boxes are used, and the cameraPlaced events that did get logged
print('\n################ TerasGardenBox events since 09-14 by actionType / soilType');
const m = {}; db3.logdata.find({game:'mhs', eventType:'TerasGardenBox', serverTimestamp:{$gte:base}}, {data:1}).forEach(e => { const k = JSON.stringify({a:(e.data||{}).actionType, s:(e.data||{}).soilType}); m[k] = (m[k]||0)+1; });
for (const [k, v] of Object.entries(m).sort((a,b)=>b[1]-a[1])) print(String(v).padStart(6) + ' ' + k);
print('\n################ ObjectInterEvent Garden Box actionTypes since 09-14');
const m2 = {}; db3.logdata.find({game:'mhs', eventType:'ObjectInterEvent', serverTimestamp:{$gte:base}, 'data.objectName':/Garden Box/}, {data:1}).forEach(e => { const k = (e.data||{}).actionType + ' @ ' + (e.data||{}).objectName; m2[k] = (m2[k]||0)+1; });
for (const [k, v] of Object.entries(m2).sort((a,b)=>b[1]-a[1])) print(String(v).padStart(6) + ' ' + k);
print('\n################ sample full documents (one crash event, one TerasGardenBox, one Garden Box ObjectInterEvent)');
print(JSON.stringify(ev[ev.length-1]||{}).slice(0, 3000));
print(JSON.stringify(db3.logdata.findOne({game:'mhs', eventType:'TerasGardenBox', serverTimestamp:{$gte:base}})||{}).slice(0, 1500));
print(JSON.stringify(db3.logdata.findOne({game:'mhs', eventType:'ObjectInterEvent', serverTimestamp:{$gte:base}, 'data.objectName':/Garden Box/})||{}).slice(0, 1500));
