# MHS Bridge WebGL Template — Build-and-Run Fallback

Standalone document. Not part of the sensitivity/invert pass series. Kept
here for distribution alongside the rest of the handoff docs.

Audience: the developer who owns the WebGL templates and the build/hosting
pipeline — particularly whoever introduced `MHS Bridge` and
`MHS Bridge - iPad Test` and their multi-build detection logic.

---

## What was wrong

Attempting **Build and Run** from a Build Profile that targets the
`MHS Bridge` template produced this in the browser when the build was
served locally by Unity's built-in WebGL server:

> **Could not determine build name from URL.**
> Set `UNITY_BUILD_NAME` at the top of the `<script>` section in
> index.html.

The error is emitted by `detectAndLaunch()` in the template's
`index.html`.

### Root cause

`MHS Bridge/index.html` (and the identical `MHS Bridge - iPad Test/index.html`)
intentionally avoids Unity's `{{{ LOADER_FILENAME }}}` build-time
replacement. Instead, it figures out which Unity loader to load at runtime
using three detection strategies, in order:

1. A `<meta name="unity-loader" content="…">` tag (manual override).
2. A `UNITY_BUILD_NAME` JS variable on line 348 (manual override).
3. The URL folder name — parses the last segment of
   `window.location.pathname` and assumes the page lives at
   `/<buildname>/index.html`.

Strategy 3 is the production multi-build hosting pattern: each unit lives
under its own folder on CloudFront/S3, and the folder name matches the
loader name (e.g. `/unit2/index.html` loads `Build/Unit2.loader.js`).

Locally, **Build and Run** uses Unity's built-in WebGL server and serves
the build directly at the URL root, e.g. `http://localhost:55123/`. The
pathname is just `/`, so strategy 3 has no folder segment to extract.
Neither manual override is set by default, so all three strategies fail
and the template emits the error.

This makes `MHS Bridge` unusable for routine in-Editor local validation —
the very workflow most developers use to verify a change before deploy.
The other templates in this project (`Adroit`, `Adroit - Prod`,
`BetterMinimal`) all use the standard `{{{ LOADER_FILENAME }}}` placeholder
and work fine with Build and Run.

---

## What was changed

A small fallback strategy was added to `detectAndLaunch()` in both
`MHS Bridge` templates. The fallback uses Unity's `{{{ LOADER_FILENAME }}}`
replacement at build time — the same mechanism the other templates rely
on — but **only as a last resort, after the existing detection strategies
have all failed**. The existing production code path is unchanged.

The new strategy is implemented as a single helper. **Important** —
Unity's WebGL preprocessor scans the entire template file for
triple-brace placeholder patterns and evaluates each match. It does
**not** respect JS comment or string-literal boundaries, so the file
must contain exactly **one** such pattern (the legitimate substitution
target on line `var unityLoaderFilename = …`) and the detection of an
un-substituted placeholder is done by checking for a `{` character,
which never appears in a real loader filename. The first version of
this fix used an `indexOf('…')` check that quoted the triple-brace
prefix directly; that introduced a second placeholder pattern in the
file and caused Unity's preprocessor to fail the build with
`SyntaxError: Invalid or unexpected token`. Lesson recorded below in
"Pitfalls".

```js
// Fallback: Unity replaces the LOADER_FILENAME placeholder below at
// build time with the actual loader filename (e.g. "Unit2.loader.js") for
// any WebGL build that uses this template. When the page is served as a
// shared parent that was NOT produced by a Unity build (e.g. a wrapper
// index hosted above a /<unit>/ tree), the placeholder is never replaced
// and the leftover brace characters let us detect that and skip this
// branch. NOTE: Unity's WebGL preprocessor parses the triple-brace
// placeholder pattern anywhere in this file (including JS strings and
// comments), so this file must contain exactly one such pattern — the
// legitimate substitution target on the next line. Detection of an
// un-substituted placeholder is done by checking for a "{" character,
// which never appears in a real loader filename.
function tryUnityLoaderFilename() {
  var unityLoaderFilename = '{{{ LOADER_FILENAME }}}';
  if (unityLoaderFilename && unityLoaderFilename.indexOf('{') === -1) {
    startUnity(buildUrl + '/' + unityLoaderFilename);
    return true;
  }
  return false;
}
```

`detectAndLaunch()` calls `tryUnityLoaderFilename()` in three places —
once for each error branch — before showing the error message:

1. Inside the `.then(r => …)` failure branch of the folder-name HEAD fetch
   (where the folder name was set but no loader was found at the expected
   URL).
2. Inside the `.catch(…)` of the folder-name HEAD fetch (where the fetch
   itself failed, e.g. CORS, network).
3. In the `else` branch where no folder name could be extracted from the
   URL — the **Build and Run** path that originally triggered the bug.

The detection order is now:

| # | Strategy | When it wins |
|---|---|---|
| 1 | `<meta name="unity-loader">` | Explicit override in the page |
| 2 | `UNITY_BUILD_NAME` JS var | Explicit override in the script |
| 3 | URL folder name (`/<buildname>/`) | Production multi-build hosting |
| 4 | `{{{ LOADER_FILENAME }}}` replacement | Build and Run, single-build hosting, any case where this template was used to produce the served page |
| – | (Error) | Page was not built by Unity AND no other strategy resolved |

---

## Why this is safe for production

The new fallback is only consulted **after** the existing strategies. It
changes behavior only in error cases — situations where, before this fix,
the user would have seen the error message anyway.

There are two production scenarios to think about:

### Scenario A — Each unit is its own Unity build, deployed under its own folder

E.g. `/unit1/index.html`, `/unit2/index.html`, … each produced by its own
Unity Build Profile using this template.

* **Before:** strategy 3 (URL folder name) resolves first. Loader URL
  becomes `Build/Unit2.loader.js` from folder `unit2`. Works.
* **After:** strategy 3 still wins first. No behavior change. Even if it
  hadn't, strategy 4 would have produced the same loader URL because the
  per-build index.html has its own `{{{ LOADER_FILENAME }}}` replaced to
  `Unit2.loader.js`. The fallback is redundant in this scenario but
  consistent.

### Scenario B — A shared wrapper page lives above multiple per-unit builds

E.g. `/index.html` (a hand-authored page or a non-Unity file) routes to
`/<unit>/` builds.

* **Before:** the wrapper page wasn't built by Unity, so
  `{{{ LOADER_FILENAME }}}` is never replaced. Strategy 3 (folder name)
  is the only thing that resolves loaders for the wrapper, by detecting
  the URL path the user is on.
* **After:** strategy 4 detects the unreplaced literal `{{{ … }}}` (the
  guard `indexOf('{{{') === -1` fails) and returns `false`, leaving
  strategy 3 to take over exactly as before. No behavior change.

### Scenario C — Build and Run (new working case)

E.g. local Unity-served URL `http://localhost:55123/`.

* **Before:** no folder name → "Could not determine build name from URL."
  error.
* **After:** strategy 3 fails as before. Strategy 4 sees the
  `{{{ LOADER_FILENAME }}}` replaced to `Unit2.loader.js` (or whichever
  build's loader Unity wrote), loads it, and the build runs.

---

## What was not changed

* **Existing production code path.** Strategies 1, 2, 3 are untouched.
* **Default values.** `UNITY_BUILD_NAME` is still `null` by default; no
  meta tag is added; folder-name parsing logic is unchanged.
* **The `findLoaderScript()` helper** — it still only checks the meta
  tag, as before. The new fallback is a separate helper to keep
  responsibilities cleanly separated.
* **Error messages.** They fire only when all four strategies fail, which
  is now a genuinely broken state (no Unity build artifacts findable
  anywhere).

---

## Files touched

* `Assets/WebGLTemplates/MHS Bridge/index.html`
* `Assets/WebGLTemplates/MHS Bridge - iPad Test/index.html`

Both edits are structurally identical. They add one new helper function
(`tryUnityLoaderFilename`) above `detectAndLaunch()` and insert three
`if (tryUnityLoaderFilename()) return;` calls inside `detectAndLaunch()` —
one in each of the three pre-existing error branches. Whitespace and
existing comments are preserved.

---

## Verification

In the Editor:

1. Pick any Build Profile that targets the `MHS Bridge` template (e.g.
   `MHS Unit Loader`, `Unit 1 Web Resize Test`, …). Build and Run.
2. The build serves at `http://localhost:<port>/`. Pathname is `/`.
   Strategies 1, 2, 3 all fail.
3. Strategy 4 succeeds: `{{{ LOADER_FILENAME }}}` was replaced at build
   time with the actual loader filename, e.g. `MHSUnitLoader.loader.js`
   or `Unit1.loader.js`. The Unity build loads and runs.

In production (if available for QA):

1. Deploy a unit build into `/<unitN>/` and visit
   `https://…/unitN/index.html`. Strategy 3 still wins first.
2. Confirm no regression: loader URL is still
   `Build/<unitN>.loader.js`, identical to before this change.

If you have a shared-wrapper deploy pattern (Scenario B):

1. Visit the wrapper. Strategy 4 short-circuits (the `{{{` guard fires)
   because the wrapper isn't part of any Unity build, so the placeholder
   is literal.
2. Strategy 3 takes over via folder-name detection — identical to before.

---

## Pattern, for the maintainer who manages these templates

A few takeaways worth carrying into other templates this team writes:

1. **Use Unity's Mustache-style replacement as a default** — it costs
   nothing in the production multi-build case (each per-build index.html
   has its own correct filename baked in) and rescues the local
   Build-and-Run case automatically. Treat it as the most reliable path
   and put it ahead of URL parsing — or, as we did here, leave it as a
   fallback if you want to preserve an existing detection order.
2. **An un-substituted-placeholder guard is what makes the fallback safe
   for shared-wrapper pages.** Without it, strategy 4 would try to fetch
   a loader URL containing the literal placeholder text, which would 404
   and confuse users. Detect by looking for a `{` character that
   wouldn't appear in a real filename.
3. **Don't rely on a single detection strategy** for a runtime concern
   that has multiple legitimate deployment shapes. Chain them, log which
   one wins (a `console.log` in each branch would help debugging in
   production), and reserve the visible error for the case where every
   strategy failed.

## Pitfalls

* **Unity's WebGL preprocessor is global, not context-aware.** It scans
  the entire template HTML for the Mustache-style placeholder pattern
  and evaluates each match as a JS expression at build time. It does
  not skip JavaScript comments or string literals — if the pattern
  appears anywhere, it's a substitution target. If your file contains
  two patterns, the preprocessor pairs them greedily (first opening
  brace triplet to the next closing triplet) and tries to evaluate
  everything in between as a single expression, which usually fails
  with `SyntaxError: Invalid or unexpected token`. Symptom in the
  Editor console:

  ```
  Preprocessor error "SyntaxError: …" occured in file
    "Assets/WebGLTemplates/.../index.html" when evaluating expression
    "<text between the two patterns>"
  ```

  **Rule of thumb:** each template file should contain at most one
  triple-brace placeholder per intended substitution target, and you
  should avoid writing the bare triple-brace sequence anywhere else —
  including in documentation comments. Detect un-substituted state by
  looking for a `{` character, not by looking for the triple-brace
  sequence itself.

* **`Debug.LogError` from a post-process callback can flip the build to
  Failed** — covered in `build-automation-postprocess-robustness.md`.
  Worth keeping in mind for any template-side reporting hooks you add
  later.

---

## Distribution / Perforce notes

Both files are pure HTML/JS — no Unity asset metadata changes. The diff
in each file is:

* one new function `tryUnityLoaderFilename` (15 lines, including comment)
  inserted between `var UNITY_BUILD_NAME = null;` and
  `function detectAndLaunch() { … }`,
* three new `if (tryUnityLoaderFilename()) return;` lines inserted inside
  `detectAndLaunch()`, one per error branch, with a short comment above
  each.

If the active Perforce branch has further changes inside
`detectAndLaunch()` (e.g. new detection strategies, telemetry hooks),
apply the same three insertions at the same logical points: immediately
before the existing `showError(…)` calls in the three error branches.
