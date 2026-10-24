// Read-only. Request and error counts per day for the state and settings APIs from the service's own stats buckets.
const S = db.getSiblingDB('stratasave');
const since = new Date(Date.now() - 35*24*3600*1000);
print('bucket durations: ' + JSON.stringify(S.api_stats.aggregate([{$group: {_id: '$bucket_duration', n: {$sum: 1}, first: {$min: '$bucket'}, last: {$max: '$bucket'}}}]).toArray()));
S.api_stats.aggregate([{$match: {bucket: {$gte: since}, stat_type: {$in: ['state_save', 'state_load', 'settings_save', 'settings_load']}}}, {$group: {_id: {d: {$dateToString: {format: '%Y-%m-%d', date: '$bucket'}}, t: '$stat_type', bd: '$bucket_duration'}, req: {$sum: '$requests'}, err: {$sum: '$errors'}, ms: {$sum: '$total_ms'}, max: {$max: '$max_ms'}}}, {$sort: {'_id.d': 1, '_id.t': 1}}]).forEach(a => print([a._id.d, a._id.t, a._id.bd, 'req=' + a.req, 'err=' + a.err, 'avg_ms=' + (a.req ? Math.round(a.ms/a.req) : 0), 'max_ms=' + a.max].join(' ')));
print('--- totals last 35 days by type');
S.api_stats.aggregate([{$match: {bucket: {$gte: since}}}, {$group: {_id: {t: '$stat_type', bd: '$bucket_duration'}, req: {$sum: '$requests'}, err: {$sum: '$errors'}, max: {$max: '$max_ms'}}}, {$sort: {'_id.t': 1}}]).forEach(a => print(JSON.stringify(a)));
print('--- slowest hourly buckets for state_save (max_ms), last 35 days');
S.api_stats.find({bucket: {$gte: since}, stat_type: 'state_save'}).sort({max_ms: -1}).limit(8).forEach(b => print(b.bucket.toISOString() + ' ' + b.bucket_duration + ' req=' + b.requests + ' err=' + b.errors + ' max_ms=' + b.max_ms + ' avg_ms=' + (b.requests ? Math.round(b.total_ms/b.requests) : 0)));
