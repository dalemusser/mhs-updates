# webgl-template-identity-100326 — the standalone page's developer identity (2026-10-03)

**Short version:** replace one file. Copy `MHS-Bridge-iPad-Test-index.html` from this folder over `Assets/WebGLTemplates/MHS Bridge - iPad Test/index.html`. No code, prefab, scene or build-profile changes.

Builds uploaded through MHS Builds are not affected: StrataHub runs the game inside its own page and does not use a build's `index.html`. This change is for running a build on its own (Build and Run, a local web server, or another host).

## What it fixes

| Issue | What was wrong | What the new file does |
|---|---|---|
| On localhost nothing is logged or saved: every request is answered 400. | The page's built-in developer identity was `mhs_developer`. The log and save services accept only a 24-character hex user id. With logging fix 1.3 the logger also holds its entries while it waits for a valid id. | The developer identity is `000000000000000000000001`, the id the Editor defaults in `MHSBridge.cs` already use. |
| Crash reports sent by the page are refused. | The report carried `playerId`; the log service takes `user_id`. | The report carries `user_id`, and is skipped when the page has no identity. |
| A login id or an email could be sent as the user id. | Identity read from StrataHub fell back to `login_id`, then `email`, when `user_id` was missing. | Only `user_id` is used. Without it the page says it is not connected. |

Nothing else in the page changes: the service URLs and the key in the localhost defaults, the way identity and service settings are fetched from StrataHub on any other host, and the loader detection from `build-automation-update-051226` are as they were.

## Check

1. Build and Run a unit, or serve a build folder from `localhost`. The loading screen's status line reads `Localhost detected — using development defaults (MHS Developer)`.
2. In the browser's network panel, the requests to the log and save services are answered 200 or 201, not 400.

## Background

The release profiles (`Release Unit 1`–`5`, `Release Testing Unit 1`–`5`), the `Web Resize Test` profiles and `Unit 1 Web iPad Test` select the `MHS Bridge - iPad Test` template; only `MHS Unit Loader` selects `MHS Bridge`. The `MHS Bridge` template received these three changes in May (`mhsbridge-userid-cleanup-052626`), the `MHS Bridge - iPad Test` template in the project did not, so the pages built since then still carried the old identity code. After this replacement the main script of the two templates is identical. `MHS Bridge` also carries the experimental iPad two-finger look script, which the game no longer needs now that it handles that input itself, so the release profiles should stay on `MHS Bridge - iPad Test`.

The file delivered here is byte for byte the copy prepared in May as `build-automation-update-051226/MHS-Bridge-iPad-Test-index.html.obsolete`. That name was a mistake: it is the current file for this template.

**Reference snapshot.** The change was made against the game team's project copy of 2026-09-28 (changelist 12449, Unity 6000.0.74f1), which the team holds; there is no separate snapshot zip for this bundle. Checked on 2026-10-03 by serving the page from `localhost` and calling the save service with the configuration the page sets up: state load and settings load are answered 200 with the new developer id, 400 with the old one. A full build with this page has not been run.
