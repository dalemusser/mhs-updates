# Read-only queries against the log service's database

Run them as in the `prod-db-readonly-query-recipe` memory: copy the script to
the log host and run `mongosh --quiet "<URI>" script.js` there. Each script has
its parameters (account id, time window, version string) at the top. All are
read-only.

| Script | Answers |
|---|---|
| `entries-for-account.js` | Entries for one account in a window: total, distinct events, max copies of one event, by-type counts, arrivals per 10 s. Exactly-once checks and duplicate floods. |
| `backlog-arrived-once.js` | For a saved queue exported with `dump_store.sh` (parsed by `parse_store.py` to JSON), whether each queued event arrived exactly once. |
| `refusals-ledger.js` | The log service's ledger of refused requests in a window: status, reason, body sizes, request interval. The refusal loop and the null-user-id first event. |
| `overwritten-details.js` | Share of entries whose details disagree with their own key or timestamp (dialogue-node, quest, puzzle-piece), split by how late they arrived, for a version string. The shared-dictionary defect, before and after. |
