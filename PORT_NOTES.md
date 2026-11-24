# Custom Launcher Backgrounds - SPT 5.0 (SPTushonka 5.0) Port Notes

Port of **Custom Launcher Backgrounds** (`com.preytolive.customlauncherbackgrounds`) from
SPT 3.10.x (TypeScript/Node server, JS mod) to SPT 5.0 (SPTushonka, C# server, DLL mod).
Version bumped **1.0.2 -> 2.0.0 -> 2.1.0 -> 2.2.0 -> 2.3.0 -> 2.4.0 -> 2.4.2 -> 2.5.0 -> 2.5.1 -> 2.6.0 -> 2.6.1 -> 2.7.0 -> 2.8.0 -> 2.8.1 -> 2.8.2 -> 2.8.3 -> 2.8.4 -> 2.9.0 -> 2.10.0 -> 2.10.1 -> 2.10.2 -> 2.10.3 -> 2.11.0 -> 2.11.1 -> 2.12.0 -> 2.13.0 -> 2.14.0 -> 2.15.0 -> 2.16.0 -> 2.17.0 -> 2.18.0 -> 2.19.0 -> 2.19.1 -> 2.20.0 -> 2.20.1 -> 2.21.0 -> 2.22.0 -> 2.23.0 -> 2.23.1 -> 2.24.0 -> 2.24.1 -> 2.24.2 -> 2.24.3 -> 2.24.4 -> 2.25.0 -> 2.26.0 -> 2.27.0 -> 2.28.0 -> 2.29.0 -> 2.30.0 -> 2.31.0 -> 2.32.0 -> 2.33.0 -> 2.33.1 -> 2.34.0 -> 2.35.0 -> 2.36.0 -> 2.36.1 -> 2.37.0 -> 2.38.0 -> 2.39.0 -> 2.40.0 -> 2.41.0 -> 2.42.0 -> 2.43.0 -> 2.44.0 -> 2.45.0 -> 2.46.0 -> 2.46.1 -> 2.46.2 -> 2.46.3 -> 2.46.4 -> 2.47.0 -> 2.48.0 -> 2.48.1 -> 2.48.2 -> 2.48.3 -> 2.48.4**.

This is a **server-side mod** at heart (the client itself is not patched) - but v2.3.0 ships optional
client-side pieces: a standalone console tool for remote players (`CustomLauncherBackgrounds.Patcher`) and a
BepInEx 6 IL2CPP plugin (`CustomLauncherBackgrounds.Gui`) for the in-game prompt. Building the plugin needs the
SPT 5.0 client interop reference assemblies (vendored locally in `client-plugin/Refs/`, gitignored).

| Part | Target |
| --- | --- |
| Project | `SPTushonka.*` **5.0.0-pre.202609141239** (the pre-release whose API matches the release server), `net10.0`, `Microsoft.NET.Sdk.Web` |
| Tested against | **SPT 5.0.0 "BLEEDINGEDGEMODS"**, build 20260914, commit `ec15a40` |
| Metadata | `Version 2.48.4`, `SptVersion ~5.0.0` |

## Why this was a rewrite, not a bump

The old mod was a server-side JavaScript mod: it hooked `ImageRouter.addRoute("/files/launcher/bg", <random image>)`
on `postSptLoad`, and the launcher of that era downloaded `/files/launcher/bg` from the server on startup.
That whole mod format (Node/TS server) no longer exists in SPT 5.0.

The old mechanism still exists in the C# server (`ImageRouter.AddRoute`), but the new launcher does not use it:
the 4.1+/5.0 launcher is a Blazor/Photino app whose background is compiled into the launcher executable
(`/images/bg.jpg` inside `SPT.Launcher.dll`, which itself is inside the single-file bundle `SPT.Launcher.exe` /
`SPT.Launcher.Linux`). The launcher settings say "Image cannot be changed for now".

## What the port does

1. **Same feature, new format** - `BackgroundSelectionService` picks a random image from
   `res/launchers/<launcherFolderToUse>/` (same folder layout and config fields as v1.0.2) and registers it on
   the `ImageRouter` at `/files/launcher/bg` at `OnLoadOrder.PostLoad` (after the core `SPT_Data/images`
   import at `Preload`, so the override wins).
2. **Launcher patching (2.1.0)** - the chosen image is also written **directly into the launcher executables**
   so the launcher itself displays it. `LauncherPatcher`:
   - locates the launcher assembly inside the single-file bundle (entry record scan, validated against `MZ`),
   - reads the launcher assembly's CLI metadata to find the `*.wwwroot.images.bg.jpg` manifest resource,
   - replaces the JPEG bytes **in place at the same length** (padded with valid JPEG COM segments, which also
     carry a `CLB-BG-PATCH:<file>` marker used to detect our own patches). Nothing else in the bundle,
     resource tables, offsets or sizes is touched - the official launcher binary stays fully intact otherwise.
   - Images larger than the slot are re-encoded (quality loop + downscale) to fit; this uses
     `System.Drawing.Common` (MIT) which is shipped with the mod and works on Windows.
   - A one-time backup (`<launcher>.original`) is created and **refreshed only when the launcher is pristine**
     (i.e. after an SPT update replaced it). Unsupported launcher builds, a running (locked) launcher or
     oversized images fail gracefully with a clear log/status message - the launcher is never left broken.
3. **Config** - `config.json` moved to the mod root (5.0 convention). Defaults and field names are unchanged
   (`modEnabled`, `consoleLogs`, `launcherFolderToUse`), so existing configs keep working; `patchLauncher`
   (default `true`) toggles the launcher writing.
4. **Web UI integration** - implements `IModBlazorMetadata` with `HomePage = /custom-launcher-backgrounds`:
   a page was added to the SIC (server web interface) that previews the selected background (live from
   `/files/launcher/bg`), plus folder/file/count info and the launcher patch status. The page also shows up
   as a card on the SIC landing page and in the launcher's Mod Pages panel / tray menu.
5. **Config editor integration** - `IConfigEditorConfigProvider` registers `config.json` so it can be edited
   in the browser on the SIC Configs page; applying changes re-picks the background at runtime.
6. **Remote patching (2.2.0)** - for players whose launcher runs on another computer, the mod prepares the
   ready-to-apply payload (fitted + padded to the launcher slot) and serves it at `/files/launcher/bg-fitted`.
   A small standalone tool, `CustomLauncherBackgrounds.Patcher` (source in `patcher/`, shares `LauncherBundle`
   and `LauncherPayload` with the mod so results are byte-identical), is published for Windows and Linux into
   the mod's `wwwroot/LauncherPatcher/` and offered for download on the SIC page. It downloads the payload,
   pads it to the local slot if needed and patches the local launcher files with the same backup/verify rules.
   Server address: `--server https://host:6969` or auto-detected from `user/Launcher/LauncherSettings.json`.
7. **In-game launcher update (2.3.0)** - the mod now also serves a complete patched `SPT.Launcher.exe`
   (`/files/launcher/clb-launcher`, built from the server's own launcher file + the same fitted payload) and
   a small info JSON (`/files/launcher/clb-info`: image, size, sha256, url). An optional **client plugin**
   (`client-plugin/`, BepInEx 6 IL2CPP, `com.preytolive.customlauncherbackgrounds.gui`) shows a small menu
   window in-game that asks whether the player wants to update their launcher to this server's background.
   Accepting downloads the patched launcher, verifies it (MZ, size, sha256, marker), backs up the current
   launcher (`.original`) and installs it (temp-file swap + verify). It reads the backend URL from the game
   launch args, auto-detects the server, avoids asking when the image is unchanged (config) and is
   distributed both in the mod package (`BepInEx\plugins\`) and as a mod-page download. Because the static
   file middleware only serves known extensions and the file router's mime table is small, the awkward
   downloads (`.dll`, `.cmd`, extension-less linux binary) are exposed as octet-stream `.bin` copies at
   `/files/launcher/clb-plugin`, `clb-patcher-linux`, `clb-patcher-cmd`.
8. **Close-the-game update flow + Corter-ModSync integration (2.4.0)** - the launcher is now only ever
   written while the game is closed:
   - The in-game prompt downloads and validates the patched launcher, then **stages** it and lets an
     external applier do the swap after the game exits. With Corter-ModSync present it stages
     `<gameRoot>/ModSync_Data/PendingUpdates/SPT_Runtime/SPT.Launcher.exe` and starts
     `ModSync.Updater.exe --silent <gamePid>` (ModSync's own pipeline: it waits for the game to exit, moves
     PendingUpdates onto the install and cleans up; its prepatcher would also apply leftovers at the next
     game start). Without ModSync it downloads the standalone patcher and runs it in a new
     `--apply-staged <launcher> <staged> --wait-pid <pid>` mode (waits, then swaps with the usual
     `.original` backup + verify, logging to `SPT_Runtime\clb-launcher-update.log`). The plugin then calls
     `Application.Quit()`.
   - **Launcher-closing (2.4.1):** the SPT launcher stays open in the tray by default while playing and
     locks its own executable, which blocks the post-exit swap (verified: ModSync.Updater logs
     "Error while attempting to copy updated files", keeps the pending file, the target is untouched).
     `LauncherProcessCloser` (shared source, linked into the plugin and the patcher) therefore closes the
     launcher first: the plugin closes it right before spawning the applier, and the `--apply-staged`
     helper closes it (again) after the game exits, just before the swap, with a graceful
     `CloseMainWindow()` and a forced `Kill()` fallback. Result: close game -> close SPT launcher ->
     apply the patched launcher.
   - Server side: `ModSyncIntegration.cs` adds a served path to `user/mods/Corter-ModSync/config.jsonc`
     (once, idempotent, with a `config.jsonc.clb-backup` backup): `../SPT_Runtime/SPT.Launcher.exe`, name
     "SPT Launcher (custom background)", enabled + optional (F12 toggle), headless:false, restartRequired
     (default true). ModSync then distributes the current patched launcher to every player and applies it
     after the game closes - no client plugin required. The plugin also creates the `.modsync-bak` restore
     file so unticking/retiring the entry restores a working launcher instead of deleting it.
   - New config flag `modSyncIntegration` (default true); status shown on the SIC page.
9. **Background rotation (2.5.0)** - the launcher rotates its background while it is open:
   - The launcher's webview rejects the server's self-signed HTTPS (verified: fetch + `<img>` to
     https://127.0.0.1:6969 both fail), but the launcher page itself is plain `http://localhost`, so rotation
     is served over a plain-HTTP helper instead: `LauncherRotationServer` (raw TcpListener - no URL ACLs)
     answers `GET /bg.jpg` with the current image and re-picks a random image every `rotationSeconds`.
   - The launcher learns to poll it via a same-length swap of the embedded `wwwroot/js/app.js`:
     `LauncherPatcher.BuildRotationScript` writes a minified copy of the original script loader (the
     launcher's own front-end scripts stay wired up) plus a compact rotator that only swaps the background
     after a successful preload (no broken-image states) and pads with spaces to the exact 607-byte slot.
   - Config: `rotationSeconds` (default 300, 0 = off), `rotationHttpPort` (default 6970), `rotationBindAll`
     (default false - loopback only). Local launchers poll `http://127.0.0.1:<port>`; downloaded (remote)
     launchers get `http://<backend-ip>:<port>` baked (needs rotationBindAll + backend ip).
   - The helper path (`--apply-staged`) and the downloadable exe carry the rotation script too
     (dual-region swap: bg.jpg + app.js).
10. **Video backgrounds + crossfade transitions (2.6.0)** - the rotation pool now also accepts `.mp4` and every
   switch crossfades:
   - `SupportedExtensions` gained `.mp4`; the rotation server serves the current item at `/bg.jpg` (images)
     or `/bg.mp4` (videos, `video/mp4`) and reports the kind at `/`; the embedded fallback image always comes
     from a still (video picks fall back to a random image for the static slot; video-only folders keep the
     previous still). Media responses use `Cache-Control: private, max-age=30` so the script's probe and the
     layer's load share a fetch.
   - The rotator script moved to the **navFade.js** slot: its comments and indentation are stripped (1940 ->
     787 bytes of code) which leaves room for the video/crossfade logic; `app.js` stays original, and a 2.5.x
     rotator found in `app.js` is restored from the pristine backup so two rotators never fight.
   - Crossfade layers: the base `<img>` always holds the last committed background; an overlay `<img>` fades
     in for image changes (then hands over to the base), and a muted looping `<video>` layer fades in for
     videos and is hidden on image ticks. Both use `transition:opacity 1s`; inserts sit right after the
     background image inside its parent so they stay below the launcher UI.
   - `LauncherBundle`/`LauncherPayload` now expose all script slots (navFade.js/app.js); the downloadable exe
     and the `--apply-staged` helper carry every matching script slot over to the player's launcher.
11. **Launcher-build check for re-prompting (2.5.1)** - the server publishes a stable `build` id with the
   launcher info (sha256 of the pristine launcher file + rotation url/settings + mod version - the random
   image is deliberately excluded). The client prompt only stays silent while that build matches what the
   player last applied, so a mod update, an SPT update or a changed rotation configuration makes the prompt
   appear once more (with a "the server has updated its launcher" message); "Don't ask again" still
   suppresses everything. Logic lives in `client-plugin/PromptDecider.cs` (pure; unit-tested via the
   harness `decide` mode); the SIC page shows the current build id.
12. **Backgrounds live directly in `res` (2.6.1)** - the 35 default images moved from
   `res/launchers/eftConceptArtwork/` to the `res` folder itself, which is now the default playlist
   (`launcherFolderToUse` empty/`.` = `res`; a name = `res/<name>` with a fallback to the legacy
   `res/launchers/<name>` layout so older installs keep working). The SIC page and README were updated
   accordingly.
13. **Images/Videos folders + ordered rotation (2.7.0)** - `res/Images` holds the default images renamed
   to `1-bg.png` ... `35-bg.png` (converted to real PNG, 900x450, ~19 MB total), `res/Videos` is the
   home for `.mp4` clips (ships with a readme placeholder). The default playlist is
   `res` root + `res/Images` + `res/Videos`; file names are ordered with a natural comparer (2-bg.png
   before 10-bg.png) and the rotation server now walks the playlist **in order** (`index+1`), starting at
   the random item picked for the server run - so up to 100 numbered images (more work too) form a proper
   slideshow with the crossfade on every switch; the log shows `(n/total)` on each step.
14. **Web GUI editor (2.8.0)** - the mod page is now interactive (`@rendermode InteractiveServer`) and a
   full editor: settings (switches/numbers/folder field + Save & apply -> updates the shared singleton,
   writes config.json via `ConfigRegistration.SaveConfigAsync` and calls `Apply()` live), a media table for
   `res/Images`/`res/Videos` (type/size/pixels, two-step delete) and an upload form with the
   image/video formatter (`MediaFormatter`: convert to PNG/JPEG, resize/crop to 900x450, 1920x1080 or keep
   the original size; mp4 files get a container sanity check). New uploads are numbered `(n)-bg.*`
   automatically (`MediaLibrary`), and every change re-applies the mod. Pure logic lives in testable files
   (`NaturalOrder`, `MediaLibrary`, `MediaFormatter`).
15. **Responsive rotation (2.8.1)** - a user reported "frozen" backgrounds on a real install: the old
   rotator (v3) polled the media URL every `rotationSeconds` (300 s by default) while the server also
   rotated every 300 s, so phase drift made a change take up to ~10 minutes to appear. The rotator script
   (v4) now polls a tiny `GET /state` endpoint ("<kind>|<name>", no-store) every 10 s and only downloads
   media when the string changed - rotation shows up within ~10 s at ANY configured interval, launchers
   stay idle (no needless downloads) in between, and a change to `rotationSeconds` no longer needs a
   re-patch to take effect. Default `rotationSeconds` lowered to 60 s (existing config files keep their
   value). The detached video probe was dropped to fit the navFade.js slot (1104 of 1153 bytes).
16. **Rotation logging follows consoleLogs (2.8.2)** - the per-step line
   `Mod: CustomLauncherBackgrounds: background rotation -> 12-bg.png (12/35)` was logged unconditionally,
   contradicting `"consoleLogs": false`. It is now only emitted when `consoleLogs` is true (the one-time
   startup line with the playlist size/interval and all failure warnings stay visible) - so a short
   rotation interval no longer spams the console of hosts who keep the default quiet setting.
17. **Page-scoped MudBlazor providers (2.8.3)** - the editor's selects (image size/fit/format) are
   popovers and failed with "Missing <MudPopoverProvider /> in the active render scope" because the SIC
   layout's providers live in a different render scope than the page-level `InteractiveServer` tree.
   `<MudPopoverProvider />` + `<MudDialogProvider />` + `<MudSnackbarProvider />` are now declared on the
   page itself (MudBlazor's documented per-page pattern).
18. **Remove the background overlay blur (2.8.4)** - the launcher's own `backgrounds.css` renders a
   `div.background-overlay` in front of the artwork with `background-color: rgba(0,0,0,0.6)` and
   `backdrop-filter: blur(2px)`; users saw the artwork as soft/hazy. The mod now also carries the
   `backgrounds.css` resource as a patchable text slot (`LauncherBundle.FindScriptRegions`) and zeroes the
   overlay's blur radius in place - `blur(2px)` -> `blur(0px)`, same byte count, so nothing moves - while
   leaving the dark tint and the (unrelated) dialog backdrop blur(10px) alone. Built from the pristine
   stylesheet base like the scripts, so repeated patches stay idempotent, and the downloadable exe /
   `--apply-staged` helper carry the CSS slot to players automatically (it is in the script slot list).
19. **Chrome-free launcher (2.9.0)** - the artwork now covers the whole window. The `backgrounds.css` patch
   was extended: the stylesheet is minified (comments/whitespace - safe, no strings/comments inside rules)
   to free room for override rules appended at the end (they win by cascade + `!important`):
   `.mud-drawer{background-color:transparent;box-shadow:none;border:0}` removes the nav rail's gray panel,
   elevation shadow and its 2px right border (the "left border") while the rail icons stay, and
   `.mud-button-filled-dark{background-color:transparent;box-shadow:none;border:0}` clears the top bar's
   filled-dark button (the "+ ADD NEW SERVER" pill: dark fill plus 2px border) so the top row floats on the
   artwork. Rebuilt from the pristine stylesheet like the blur fix - idempotent, and carried to players by
   the downloadable exe / --apply-staged helper automatically. Fits: 709 byte minified css + 204 byte
   overrides = 913 of the 1148-byte slot.
20. **Borderless launcher window (2.10.0)** - users wanted the native title bar/border gone so the artwork
   fills the whole window. `LauncherWindowChrome` (shared by the server mod and the in-game plugin) removes
   the caption frame (`WS_CAPTION` off, `WS_THICKFRAME`/sysmenu kept so edges still resize and Alt+Space
   Move works) and clears the Windows 11 DWM border colour (`DWMWA_BORDER_COLOR` -> `DWMWA_COLOR_NONE`;
   `DwmSetWindowAttribute` returns S_OK verified). A server-side watcher (`LauncherWindowWatcher`, every
   4 s) applies it to any `SPT.Launcher` window on the host machine while the server runs; the in-game
   plugin applies the same tweak on players' machines when the main menu is reached (both behind the new
   `launcherWindowBorderless` flag, default true; also a switch on the mod page; the page shows the watcher
   status). Trade-off documented: a borderless window is moved with Win+Arrow/Alt+Space and closed with
   Alt+F4 (no title bar buttons) - disabling the flag leaves future windows untouched.
21. **Native window restored + tinted border (2.10.1)** - the borderless default was too disruptive (no title
   bar buttons/drag), so `launcherWindowBorderless` now defaults to **false** and the chrome helper gained a
   second setting: `launcherWindowBorderTint` (default true) colours the window border with the launcher's
   own light line shade (its `mud-border-lines-default` look, 12% white over the dark UI = #404048 /
   COLORREF 0x00484040) instead of the Windows default. The watcher reconciles both ways: it restores the
   caption when borderless is off (so windows already stripped by 2.10.0 get their title bar back) and
   removes it when on. The mod page lists both switches and the status line says which look is active; the
   plugin applies the tint too (borderless per its own flag, now also default false).
22. **Lighter frosted panels (2.10.2)** - the launcher's own mudblazor.css paints every frosted surface
   (mud-paper cards incl. the profile/stats panels, dialogs, tabs, popovers) with rgba(25,25,31,0.7). The
   appended overrides now include `.mud-paper,.mud-dialog,.mud-tabs,.mud-popover{background-color:rgba(25,25,31,.45)!important}`
   (backgrounds.css loads after mudblazor.css, so the later rule wins), lowering the panels to 45% while the
   frosted blur and the fully transparent nav rail stay. Robustness fix found while testing: the transform
   now strips a previous override block from the base before appending (an older patched file can be the
   pristine base when the backup predates a feature), so the result never overflows the 1148-byte slot and
   is skipped - count of the override block stays 1 across re-patches.
23. **Withdrawing the remote artifacts no longer dangles routes (2.10.3)** - a user hit
   `Error handling request: /files/launcher/clb-info / Could not find file ... cache\launcher-info.json`
   after toggling `patchLauncher` off (their config showed `patchLauncher: false` and the cache files were
   gone): Apply() DELETED the three cache files on disable/failure, but the /files/launcher/clb-* routes
   registered earlier in the same process remained - and ImageRouter has no route-removal API, so requests
   hit `HttpFileUtil.SendFileAsync` -> `FileInfo.Length` -> FileNotFoundException. Fix: cache artifacts are
   never deleted any more; `WithdrawRemoteArtifacts` overwrites the info JSON with `{}` (the plugin treats
   that as "no update offered") and leaves the payload files stale-but-present, so no registered route can
   ever point at a missing file. Applied to all five paths (disabled, payload build failure, no local
   launcher, downloadable build failure, unexpected exception). DeleteIfExists removed entirely.
24. **Configurable panel opacity (2.11.0)** - the frosted-panel opacity (hardcoded 45% in 2.10.2) is now the
   config value `launcherPanelOpacityPercent` (default 45, 0-100, also a numeric field on the mod page with
   a note that it is baked in at patch time). `LauncherPatcher.BuildStyleOverrides(percent)` formats the
   alpha invariantly (`0.##` - "0.45", "0.2", "0", "1") and the patch detail line reports
   `- panels at N%`. Out-of-range values are clamped; the stylesheet slot fit-check still guards the size.
25. **Frosting fades with the fill (2.11.1)** - a user set launcherPanelOpacityPercent to 0 and reported "not
   working": the tint was gone but the launcher's `backdrop-filter: blur(20px)` still frosted the cards, so
   they looked like blurred patches (verified live: bg rgba(25,25,31,0) + blur(20px)). The appended override
   now also sets `backdrop-filter` in the same rule, scaled with the fill: 0% -> blur(0px) (fully clear),
   45% -> blur(13px), 70%+ -> the stock blur(20px). The dialog is in the same rule so the stylesheet slot
   fit-check still applies.
26. **rotationHttpIP (2.12.0)** - new `rotationHttpIP` config: an explicit IP (LAN, VPN/Tailscale, public
   address, or "0.0.0.0") the rotation server listens on. Loopback is bound alongside it (so the local
   launcher keeps polling 127.0.0.1), launchers downloaded by other players bake the configured IP as the
   remote URL, and the SIC status shows "(other computers: http://<ip>:<port>)". `rotationHttpIP` takes
   precedence over `rotationBindAll`; an unparseable value logs a warning and falls back.
   `LauncherRotationServer` now holds multiple TcpListeners (one per bind address).
27. **Image/video use toggles + separated uploads (2.13.0)** - new `useImages` / `useVideos` config booleans
   (defaults true) shape the rotation playlist: images-only, videos-only, or - when both are on - a strict
   round-robin that alternates image, video, image, video... (the shorter list wraps). The embedded/classic/
   download background stays an image whenever any still exists; with images off the launcher keeps its
   default baked art and rotation serves clips. The web editor now has separate **Images** and **Videos**
   cards, each with its own toggle, its own file list/delete and its own upload (images formatted to the
   chosen size, videos validated + stored), replacing the single combined media card.
28. **Video formatting (2.14.0)** - the Videos upload gained a formatter like Images: target size
   (900x450 / 1920x1080 / keep) and fit (crop-fill / stretch). Implemented with an external ffmpeg on the
   server machine: `MediaFormatter.FormatVideo` resolves ffmpeg (`videoFfmpegPath` config or the PATH /
   C:fmpeg), scales/crops via a `-vf` filter and re-encodes H.264 mp4 (libx264, crf 23, yuv420p, aac).
   No ffmpeg (or 'keep original size') => the clip is stored unchanged with a clear note, so uploads never
   fail. `MediaFormatter.FindFfmpegExecutable` checks the configured path, then PATH, then common locations;
   a 120s timeout guards against hangs. The editor Videos card has the size/fit selects and an ffmpeg-path
   field.
29. **Per-kind auto-numbering (2.15.0)** - uploads are named per kind now: `MediaLibrary.NextNumber(modDir, kind)`
   counts only that kind's folder, so the first image becomes `1-bg.png` and the first video `1-bg.mp4`
   (previously the counter was shared: 35 images pushed the first video to `36-bg.mp4`). Subsequent uploads
   take the next free number within that kind, and the kind (extension for the folder+extension choice,
   mp4 container magic for videos, image decode for images) decides where the file lands. Editor help texts
   updated.
30. **Gap closing (2.16.0)** - deleting a file used to leave a hole (1-bg, 3-bg) and the next upload took
   max+1. `MediaLibrary.Renumber` now compacts each kind folder to a contiguous 1..N `(n)-bg.ext` sequence
   (ascending renames so nothing collides; non-pattern names and occupied targets are left alone) and
   `Apply()` runs it at the start of every apply - so a web-editor delete, an Explorer delete or a manual
   rearrange self-heals on the next apply. Logs `renumbered N media file(s) to close numbering gaps` when
   anything moved.
31. **Ordered start toggle (2.17.0)** - the rotation has always played the playlist in natural numeric
   order (1-bg, 2-bg, ... 10-bg), but it seeded its index from the randomly chosen baked background, so it
   could start at 12-bg and wrap. New `randomRotationStart` config (default true = original behaviour);
   with it off the rotation always starts at `playlist[0]` - the lowest-numbered file (1-bg) - and then
   advances in number order. Also a switch on the mod page ("Random start point (off = always start at
   1-bg / lowest number)").
32. **Duplicate detection (2.18.0)** - `MediaLibrary.FindDuplicate` compares uploaded content with what is
   already in the matching kind folder (length first, then SHA-256, so only same-size files get hashed -
   cheap even for large video folders) and the import paths skip storing a copy: the upload reports
   `x.png: identical to 1-bg.png - not added (no duplicate copy)` and no number is consumed. The check runs
   on the FINAL bytes (after image formatting / video re-encode), so re-uploading a source that formats to
   the same result is also caught. Page result alerts now colour these notices amber (Severity.Warning) via
   a small ResultSeverity helper.
33. **Same-source-name override (2.19.0)** - uploads now remember where they came from: `Import` records
   `{ file, original upload name, sha256 }` in `cache/upload-origins.json` and the import paths check it
   after the duplicate test. Uploading "killa.png" a second time with CHANGED content replaces the stored
   `5-bg.png` in place (same number, hash refreshed) instead of adding another file; re-uploading that new
   version is then recognised as a duplicate. The manifest follows the 2.16.0 renumbering (renamed files
   keep their origin) and drops entries on delete; `FindStoredForOriginal` ignores entries whose file has
   disappeared, so a manual removal just means the next upload imports fresh.
34. **In-game prompt restyle (2.19.1)** - the prompt copy is now exactly: title **"Custom Launcher Backgrounds"**
   (bold + highlight bar), the line `The server has updated it's launcher since your last session.`,
   **"Launcher is outdated"** (bold + highlight) and the buttons **Update / Skip / Never**. The panel is drawn
   on a light grey background with dark text throughout (labels, status phases and Retry/Close restyled) -
   `EnsureStyles` builds 1x1 textures per colour plus bold/custom GUIStyles once at the first OnGUI call, and
   `DrawHighlighted` centres text over a soft yellow bar. Build note: bold text needs `FontStyle`, so
   `client-plugin/Refs/UnityEngine.TextRenderingModule.dll` is now referenced (from the client refs bundle).
35. **2 GB video uploads (2.20.0)** - the video cap went 512 MB -> 2 GB, which needed the whole video path to
   stop holding files in memory (a byte[] cannot even exceed ~2.1 GB): the editor now streams each upload
   straight to a temp file (`OpenReadStream(2 GB)` -> `File.Create`), `ImportMediaFiles`/`ImportVideoFile`
   work from those paths (mp4 head check via `LooksLikeMp4File`, `FindDuplicateFromFile`, `ReplaceFromFile`,
   `ImportFromFile`, `HashFile` - all streamed), and `FormatVideoFile` runs ffmpeg file-to-file with a
   timeout that scales with the input size (2 min + 200 ms/MB, capped at 30 min) instead of the old 120 s.
   The rotation server no longer `ReadAllBytes`-es the current item either: it holds the path and streams it
   from disk per request (`SendFileResponse`, length read at request time). Temp files/dirs are deleted
   afterwards; images keep the 512 MB cap.
36. **ffmpeg auto-detection in the mod folder (2.20.1)** - `FindFfmpegExecutable(configuredPath, modDirectory)`
   checks the mod folder first (`ffmpeg.exe`, `ffmpeg\ffmpeg.exe`, `ffmpeg\bin\ffmpeg.exe`,
   `tools\ffmpeg\ffmpeg.exe`, `tools\ffmpeg\bin\ffmpeg.exe`) and resolves a RELATIVE `videoFfmpegPath` from
   there, before falling back to the PATH and `C:\ffmpeg` installs. The service resolves once per upload and
   passes the absolute path to the formatter; the editor hint lists the spots.
37. **Video audio, video crossfade, hidden server card (2.21.0)** - the launcher script gained:
   * **audio** - videos start muted (so Chromium's autoplay policy lets them play) and are unmuted as soon as
     playback begins (`play().then(snd)`); a `visibilitychange` listener mutes both layers while the window is
     minimized/hidden and unmutes on restore;
   * **video-to-video crossfade** - two alternating video layers (`V`/`W`): the incoming clip fades in while
     the outgoing fades out (its `src` is dropped 1.2 s later), exactly like the image crossfade;
   * **`hideServerAddress`** - a text value; server cards whose text contains it are hidden, and a
     MutationObserver re-applies the hiding after every Blazor re-render. Note: MudBlazor's `d-flex` utility
     is `display:flex !important`, so the hide uses `style.setProperty('display','none','important')`.
   The script outgrew navFade.js (only ~60 bytes free after minification), so `LauncherBundle` now exposes
   `userTabset.js` (2344 bytes free) and `ApplyRotationScript` picks the slot with the most room
   (`FindBestScriptSlot`), restoring any other slot that still carries an older rotator (one rotator only).
38. **Mute button (2.22.0)** - a small Mute/Unmute pill in the launcher's top right corner. It lives in its
   own script slot (`externalLinks.js`, chosen by `ApplyMuteButton`), keeps its state in `localStorage`
   (`clbMute`) and notifies the rotator through a `clb-mute` event, so the two scripts stay independent; the
   rotator's `snd()` mutes while that flag or `document.hidden` is set. The label always shows the action
   ("Unmute" while muted). Supporting changes: the overlay styling moved into the stylesheet patch
   (`.clbov` class, saving ~110 bytes in the rotator slot) and `CompactScriptBase` now strips BOTH markers
   (`//CLB-ROTATE`, `//CLB-MUTE`) so re-patching stays idempotent; the CSS overrides were trimmed with
   shorthands (`background:`) and `.mud-tabs` dropped from the panel rule to stay inside the 1148-byte
   stylesheet slot; `.clbov` must keep explicit `width/height` (an `inset:0` only variant left replaced
   elements at their intrinsic size - caught by a screenshot).
39. **Hide-local-card toggle (2.23.0)** - the 2.21 `hideServerAddress` TEXT field shipped blank, so a user who
   expected the local card gone saw nothing change (their exe had the hide function with `H=''`). It is now a
   simple boolean, `hideLocalServerCard` (default false; on the mod page as a switch): when on, the baked
   token is `127.0.0.1` and any server card whose text contains it is hidden. The old string property is
   gone (a leftover `hideServerAddress` key in an existing config.json is simply ignored on load).
40. **Mute button colour (2.23.1)** - the pill is now `#202020` with light text (`#f2f2f6`), matching the
   launcher's dark chrome, instead of the light grey fill. Verified in the running launcher: computed
   `background-color` is exactly `rgb(32, 32, 32)`.
41. **Mute tile in the nav rail (2.24.0)** - the button moved from a floating top-right pill into the launcher's
   left rail, as the FIRST child of `.nav-bottom` (directly above the Settings gear). It wears the launcher's
   own `mud-nav-link` tile styling with `background:#202020` and a speaker icon whose slash appears while
   muted (state in `localStorage`, rotator notified through a short `cm` event; a 2 s re-placement loop keeps
   it there across Blazor re-renders). Fitting it meant: markers unified under one `//CLB` prefix (the base
   compaction strips any of them), the event/key kept short, and the script trimmed to 702 of the 720 bytes
   the externalLinks.js slot allows.
42. **Mute tile without its own fill (2.24.1)** - the tile now matches the Settings/Info icons exactly: no
   background of its own, straight on the artwork (computed `background-color: rgba(0,0,0,0)`,
   `color: rgba(255,255,255,.498)` - identical to the gear/info entries beside it). The #202020 fill was
   dropped, which also freed ~37 bytes in the tight externalLinks.js slot.
43. **Mute tile iconography (2.24.2)** - the icon is now four nested sound arcs (single path with four
   `a` subpaths, `fill="none"` + `stroke="currentColor" stroke-width="2"` set once on the `<svg>` so both
   paths inherit) and the muted mark is an **X** (`M4 8l7 8M11 8l-7 8`) instead of the diagonal slash. The
   X path toggles via `style.display`; the arcs stay visible either way. Script fits in 712 of the 720 bytes
   the externalLinks.js slot allows.
44. **Old iconography back (2.24.3)** - the 2.24.2 arcs+X were reverted to the previous speaker icon: filled
   speaker when audible, speaker + diagonal slash while muted, toggled the same way. Background stays clear
   (`rgba(0,0,0,0)` with the rail's `rgba(255,255,255,.498)` colour) and the tile stays above Settings.
45. **Hidden local card no longer holds its slot (2.24.4)** - users noticed new servers never moved into the
   first slot: the launcher wraps each server card in a `div.mud-grid-item` (the grid cell) and hiding only
   the inner `.card-container` left that cell occupying space (measured 330 x 24), so the next card went to
   cell two. `hide()` now walks one level up and hides the `mud-grid-item` itself (falling back to the card
   when the parent is not one), collapsing the cell to 0 x 0. The localStorage key was shortened to `cm`
   (both scripts) and the fetch guard/observer trimmed to stay inside the very tight userTabset slot; the
   marker is now `//CLBR` (all markers share the `//CLB` prefix the compaction strips).
46. **Volume slider in the launcher's Settings (2.25.0)** - a "Volume" row is injected into the Interface
   section of the launcher's own `/Settings` page (the second `.mud-paper.mud-elevation-3`, matching the
   launcher's row layout: label left, range input right). Its value lives in localStorage (`cv`) and `snd()`
   now applies `volume` together with the mute state (`[V,W].forEach(...)`), so the slider affects the
   rotating videos immediately - 40 on the slider gives layer volume 0.4. Injection re-runs whenever the app
   re-renders (the existing MutationObserver now calls `hide()` and `vol()`), and the initial `vol()` runs
   before the first tick. Fitting it meant a lean `insertAdjacentHTML` row and a loop-based `snd()`; the
   rotator script sits at ~2323 of the 2344-byte userTabset slot.
47. **Per-kind rotation intervals + multi-select delete (2.26.0)** -
   * **Separate intervals**: new `imageRotationSeconds` (default 10) and `videoRotationSeconds` (default 60)
     in config.json and the editor. The rotation server now holds each item for ITS kind's interval (an
     image for its seconds, a video for its own). Legacy `rotationSeconds` remains the fallback when the
     per-kind keys are absent (and 0 still disables rotation for both). The launcher poll also sped up
     (10 s -> 5 s) so short image intervals aren't skipped visually.
   * **Multi-select delete**: both the Images and Videos editors gained a checkbox column (with a select-all
     header checkbox) and a "Delete selected (N)" two-step button. New `MediaLibrary.DeleteMany` deletes the
     chosen files (same validation as the single delete) and renumbers the gaps ONCE; `DeleteMedia(IEnumerable)`
     re-applies once instead of once per file.
48. **Background veil toggle (2.27.0)** - `launcherBackgroundBlur` (switch in the editor, default true) controls
   the launcher's own `div.background-overlay` - the dark tint + backdrop-filter layer that sits over the
   image/video. When off, `ApplyBackgroundBlur` writes a tiny `//CLBB` script into the (pristine, roomy)
   app.js slot that sets `display:none` on that layer and re-applies it every 2 s; when on, the app.js slot
   is simply restored to its compacted pristine content (so flipping the toggle both ways is idempotent).
   The side bars/cards are untouched - their frosting stays on the `launcherPanelOpacityPercent` setting.
   Note: the stock 2 px blur on that layer was already zeroed (2.9.0 request), so what the toggle shows is
   the veil itself.
49. **Background opacity (2.28.0)** - new `launcherBackgroundOpacityPercent` (editor field "Background opacity %
   (image/video)", right under "Panel opacity %", default 100). The value is set on the background CONTAINER
   (found structurally as the parent of `img.background-image-cover`) so the rotating layers keep their own
   per-layer opacities and the crossfade still works; the side bars/cards are untouched. The app.js tweak
   script now carries both background tweaks (`ApplyBackgroundTweaks`: veil hiding and/or opacity), applied
   every 2 s; with both at their defaults the slot goes back to pristine content. Combined script is 258 of
   the 266 bytes the app.js slot frees up.
50. **Update prompt on every apply (2.29.0)** - the in-game prompt only appeared when the launcher build id
   changed, and that id (pristine hash + rotation url + seconds + mod version) ignores most settings, so
   re-applying in the web editor could leave players unprompted. `SaveSettingsAsync` and `Reapply()` now bump
   an explicit revision counter (`cache/launcher-revision.txt`) which is folded into the build id, so EVERY
   Save & apply / apply-button press makes clients see a newer build. A plain server restart does not bump it
   (startup Apply never calls the bump), so restarts no longer risk prompt spam. No plugin change needed -
   it already compares `info.build` against the player's last-applied build, and "Never" still suppresses.
51. **Veil percentage (2.30.0)** - the veil toggle (`launcherBackgroundBlur`) and the artwork-opacity field
   (`launcherBackgroundOpacityPercent`) were merged into ONE setting: `launcherBackgroundVeilPercent`
   (editor field "Blur/veil over the background image & video %", right under Panel opacity, default 100).
   100 = the launcher's stock veil (rgba(0,0,0,.6)) with no script at all; below 100 the script sets that
   tint scaled (50 -> rgba(0,0,0,0.3), 0 -> rgba(0,0,0,0) = fully clear artwork), re-applied every 2 s. The
   old app.js dimming/display-none path is gone. Stale `launcherBackgroundBlur` /
   `launcherBackgroundOpacityPercent` keys in existing configs are ignored on load.
52. **Window border tint follows Panel opacity (2.31.0)** - the separate "Tint the window border in the
   launcher's light shade" switch (`launcherWindowBorderTint`) is gone. `StartWindowWatcher` now derives the
   tint from the panel opacity: below `BorderTintPanelOpacityThreshold` (70 - i.e. the user has cleared the
   panels away from the stock frosted look) the window border is tinted so it blends, at 70+ the border is
   left alone (the watcher is not even started when borderless is off too). The status line on the mod page
   explains which case applies. Existing `launcherWindowBorderTint` keys in config.json are ignored on load.
53. **Image enhance on upload (2.32.0)** - new `imageEnhance` setting ("off" default, "light", "strong") with a
   dropdown in the editor above the image upload form. `ImportImage` folds it into the format options
   (`MediaFormatOptions.EnhanceAmountPercent`, default 0 so every existing call site is unchanged) and
   `MediaFormatter.Enhance` runs on the finished (resized/cropped) bitmap: a 3x3 unsharp mask on a 32bpp
   copy (`LockBits` + `Marshal`), then saturation and contrast colour matrices, then a lossless copy back into
   the 24bpp canvas before encoding. Strengths: light = 35 (sharpen 0.21, sat x1.06, contrast x1.04), strong
   = 70 (0.42, x1.13, x1.08); "off" is byte-for-byte the old behaviour.
54. **Enhance default + re-apply to stored images (2.33.0)** - a user reported "the enhance did not apply";
   their config.json (written when 2.32.0 shipped) explicitly said `imageEnhance: "off"`, and enhancement only
   runs on new uploads. Fixes: the shipped default (and the class default) is now **light**; the upload result
   lines and log say `- enhanced (light)` when it ran; and a new editor button **"Re-apply enhance to stored
   images (N)"** (`ReenhanceImages`) runs the current setting over the images already in res/Images - each file
   is reformatted with keep-size options (no resize/crop), written through `MediaLibrary.ReplaceFromFile`
   (hash refreshed) and re-applied once. Nothing happens when the setting is off (the button reports why).
55. **Re-apply used the saved strength (2.33.1)** - the 2.33.0 button read `Config.ImageEnhance`, i.e. the
   SAVED value, so a freshly picked dropdown entry was ignored (and `RefreshFromService` in the handler then
   snapped the dropdown back to the saved value - "it stays on light"). The handler is now async and saves the
   editor's settings first (`SaveSettingsAsync`), then runs `ReenhanceImages()` with the just-saved strength,
   reports it ("Enhanced N stored image(s) with strong.") and only then refreshes. The button is labelled
   "Save & re-apply enhance to stored images (N)" with a caption saying it saves first.
56. **Enhance is now real detail sharpening (2.34.0)** - a user reported "it's changing the colour, not
   enhancing the image quality". They were right: the 2.32 pass boosted saturation (x1.06/x1.13) and contrast
   (x1.04/x1.08) via colour matrices and only applied a gentle 3x3 per-channel unsharp. Both matrices are gone.
   `Enhance` now runs `LumaUnsharpMask`: the luma channel is box-blurred with a separable 5x5 window and the
   difference (luma - blurred) is added to r, g, b by the SAME amount - a pure luminance correction, so hues
   and saturation stay exactly as uploaded (no colour fringing either, which per-channel sharpening causes).
   Strength: amount = 1.4 x percent/100 (light 0.49, strong 0.98). The editor label is now
   "Image enhance (sharpen detail)" with helper text spelling out that colours are untouched.
57. **Image enhance removed (2.35.0)** - the whole feature is gone at the user's request: the `imageEnhance`
   setting (ModConfig, config.json, editor dropdown), the "Save & re-apply enhance to stored images" button and
   `ReenhanceImages`, `MediaFormatter.EnhancePercentFor`/`Enhance`/`LumaUnsharpMask` and the
   `MediaFormatOptions.EnhanceAmountPercent` field. Images are stored exactly as formatted again. Already
   stored images keep whatever they were processed with (re-upload the original to go back); a leftover
   `imageEnhance` key in config.json is ignored on load.
58. **Crop complaints fixed (2.36.0)** - a user's upload came out visibly cut. Cause: the upload form's default
   "Image size" preset was **900x450 = 2:1** while the launcher window is ~16:9, so a 16:9 source was
   cover-cropped top/bottom at import (then the launcher cropped again at display, since its layers use
   object-fit: cover). Changes:
   * default target size is now **1920x1080 (16:9 - matches the launcher window)**; the presets are labelled
     with their aspect ("900 x 450 (2:1 - the launcher's native art)", "Keep original size (no resize, no crop)").
   * new fit mode **"Fit - blurred background (nothing is cut)"**: `MediaFormatOptions.BlurFillBackground` makes
     `FormatImage` draw a cover-scaled copy into a 24px bitmap, stretch it back as a soft backdrop (plus a 70
     alpha darkening), then draw the whole image fitted on top - no pixel of the source is lost.
   * "Crop to fill" is relabelled "(full-bleed - cuts the overflow)" so the trade-off is explicit.
59. **Upload defaults (2.36.1)** - the upload form now opens on **Image size 1920x1080 (16:9)**, **Image fit
   "Stretch to size"** and **Image format PNG** (the owner's choice: nothing is cropped or letterboxed - a
   16:9 source lands undistorted, other aspects stretch to fill). Crop to fill and Fit - blurred background
   remain selectable.
60. **JPEG cleanup / deblock (2.37.0)** - reported as "light dots on the images in a pattern". Diagnosis: the
   stored PNG carries the SOURCE wallpaper's JPEG 8x8 block grid (measured block step 4.96 vs in-block gradient
   2.85 at full resolution), which becomes a visible dot/block pattern once the launcher scales the image down
   to window size. New `DeblockAmountPercent` option ("JPEG cleanup": Off (default) / Light 55 / Strong 100) in
   the image upload row: `FormatImage` runs `DeblockToBitmap` on the source before resizing - a boundary
   levelling filter that estimates the step from two pixels per side (grain cancels), skips boundaries whose
   step is 32+ (real detail) and applies a luminance-based correction (so colours never shift), touching the
   two pixels either side at half weight.
   Measurements: synthetic block grid 18.79 -> 2.32 blockiness ratio with hard-edge contrast 255 -> 255; on the
   reporter's image the 8px block step falls 4.96 -> 4.42 (light) / 3.56 (strong) while the in-block gradient
   (texture + grain) stays 2.85 -> 2.80/2.85, i.e. the grid is levelled without softening the picture.
61. **Dot-pattern investigation (no code change)** - reported as "light dots in a pattern ... maybe an overlay".
   Findings, all measured: (1) the stored image DOES carry the source JPEG's 8x8 block grid (block step 4.96 vs
   2.85 in-block) - addressed by the 2.37 JPEG cleanup; (2) a perfectly FLAT image displayed by the launcher
   still shows a faint regular dot lattice (per-channel spatial stddev 1.35 with the stock veil, 3.3 without),
   while the launcher's own flat CSS background measures 0.00 - so the lattice belongs to the WebView's image
   rasterisation, not to any element we inject; (3) ruling out our side live: removing the veil's tint,
   removing its `backdrop-filter` entirely, and forcing `image-rendering: optimize-contrast`/`pixelated` all
   left the lattice unchanged; (4) the launcher's own baked background (base img, no mod layers) shows it too.
   Conclusion: it is Chromium/Skia dithering when it scales the (large) image into the surface - not
   switchable from CSS/JS. Mitigations documented for the user: the veil multiplies the amplitude (0.4 at the
   stock 100%), and the JPEG cleanup removes the image's own block grid.
62. **"Maximum" JPEG cleanup (2.38.0)** - the JPEG cleanup dropdown gained a fourth choice that also smooths grain:
   `DeblockAmountPercent 100` + `DenoiseAmountPercent 45`. `SmoothGrain` blends a 3x3 average over each pixel in
   proportion to how flat its 3x3 neighbourhood is (same gate idea as the deblock), so flat speckle smooths and
   real edges/texture stay. Harness: on a grainy test image the speckle metric falls 22.97 -> 12.53 while the
   hard-edge contrast stays 255 -> 255; the reporter's wallpaper goes from block step 3.56 / grain 2.85 to
   3.03 / 3.02 (strong) and 3.19 / 2.68 (maximum) - note most of that wallpaper's speckle is real artwork
   detail (rain, film grain), which the flatness gate deliberately preserves.
63. **Image format detection (2.39.0)** - reported that "keep original format does not fix it": the old logic followed
   the file NAME's extension, so a JPEG wallpaper saved as `.png` was stored as PNG with its JPEG artifacts
   intact. `MediaFormatter.DetectImageFormat` now sniffs the container from the bytes (JPEG `FF D8 FF`, PNG,
   GIF, BMP, WEBP) and the `source` ("keep original") output format follows the DETECTED format - JPEG stays
   JPEG, everything else becomes PNG; the explicit PNG/JPEG choices still win. The editor's dropdown now reads
   "Auto - detect from the file (JPEG stays JPEG, others PNG)" and is the default, and the upload result notes
   mismatches ("- file is really jpg (name said png)").
64. **WebView image dithering removed (2.40.0)** - the faint dot lattice reported on the launcher background is
   now GONE. Root cause (measured with a flat test image on a live launcher): Chromium dithers when it
   rasterises a large `<img>` layer - a perfectly flat image showed a regular dot lattice (per-channel spatial
   stddev 1.35 under the stock veil, 3.3 raw) while the launcher's flat CSS background measured 0.00. Ruled
   out: the veil tint, its `backdrop-filter`, `image-rendering: pixelated`/`optimize-contrast`, serving the
   image at the window's exact size, and a CSS background instead of an img. The same pixels drawn through a
   `<canvas>` measured 0.00 - so the rotator now draws stills into a canvas (`C=m('canvas')`, sized to the
   image, drawn 1:1, fitted by the existing `.clbov` `object-fit: cover`), keeps it visible while a still is
   current and fades it out only when `vid()` puts a video on top. The old base-`<img>` handoff was dropped
   (it was itself a dithered layer). Byte budget was tight (script 2326 of 2344): savings came from a
   `[V,W].forEach` for the video properties, `[C,V,W].forEach((e,k)=>...)` for the opacities, an arrow in the
   fetch chain, `cssText+=` for the card hiding, and a slimmer volume row; the canvas version fits with room
   to spare. Verified live: flat image 1.35 -> 0.00 spatial stddev, real artwork renders correctly, and a
   rotation with images + videos shows canvas opacity 1 for stills / 0 for videos with playback, crossfade,
   mute, hide-card and the volume slider all still working.
65. **Video audio stops with the video (2.41.0)** - reported as "when a video ends I still hear the audio until
   another one plays": `img()` only faded the video layers out (opacity 0) and, with `loop` set, they kept
   looping audibly until the next `vid()` muted and cleared them. `img()` now also does
   `[V,W].forEach(x=>{x.pause();x.muted=1})`, so a still silences them immediately while the frozen frame
   still fades out cleanly; the next video resumes as before. Script 2309 of 2344 bytes.
66. **Stills crossfade too (2.42.0)** - "the smooth fades seem to not work all the time": only image<->video
   transitions faded; a still->still swap redrew the canvas instantly, which is most transitions in a
   picture-heavy rotation. `img()` now dissolves through the launcher's own base layer (it sits behind our
   canvas): point the base at the INCOMING image, fade the canvas (still showing the outgoing still) out over
   1 s, then redraw the canvas with the incoming image and fade back in. The video layers are paused, muted and
   faded out in the same tick, so every transition type now blends. Cost: img() +58 bytes, paid for by
   `localStorage.cm`/`.cv` property access, `document.onvisibilitychange` and a shorter base selector
   (script 2322 of 2344).
67. **Prompt GUI restyled to the dark-card design (2.43.0)** - the in-game prompt now matches the requested
   reference: rounded dark card (rgba(33,36,43,.99), subtle 1px edge, radius 16), white bold centred title,
   grey body copy, one **full-width blue primary button** (rgb(53,120,242); hover rgb(75,140,248), active
   rgb(40,100,218); radius 10) for Update/Retry, and quiet dark buttons (rgba(25,28,33) with a 1px edge) for
   Skip/Never/Close beneath it. `MakeRoundedTexture` generates antialiased rounded-rect textures (optional 1px
   edge) used as 9-sliced GUIStyle backgrounds (border = radius + 1) so corners stay round at any size; the
   quoted phrases keep bold + highlight, now as a soft blue bar. Status phases use a bright headline
   (`_strongStyle`) over grey lines. New: `_panelStyle`, `_highlightBarStyle`, `_strongStyle`,
   `_primaryButtonStyle`, `SecondaryButtonHeight`; `_panelBorderTexture`/`MakeTexture` removed.
68. **Panel opacity extended to SIC / mod-pages panels (2.44.0)** - "these panels are not clear": the override
   only matched `.mud-paper,.mud-dialog,.mud-popover`; the frontend's own panel containers are not MudBlazor
   papers (`.mod-pages-panel` measured `rgb(39,39,47)` = fully opaque) and SIC uses `sic-` prefixed classes.
   The selector is now `.mud-paper,.mud-dialog,.mud-popover,.mod-pages-panel,[class*=sic-]:not([class*=sic-] *)`
   - the `:not()` guard keeps nested SIC elements transparent so tints never stack. Paid for by
   `StripDecorativeArtwork`, which drops the four 2%-opacity decorative corner artworks from the minified base
   (~300 bytes freed; the slot was exactly full: 709 base + 439 overrides = 1148).
69. **Character stats panel follows the setting (2.45.0)** - "not applied to the OVERALL PMC SCAV character stats tab
   when selecting a profile": that panel is a **`.mud-tabs`** container (633x263, tab bar + stats grid) and it
   computed `rgba(25,25,31,0.7)` - the launcher's stock 70% - because `.mud-tabs` had been dropped from the
   panel override in an earlier byte trim. Added back: the rule is now
   `.mud-paper,.mud-tabs,.mud-dialog,.mud-popover,.mod-pages-panel,[class*=sic-]:not([class*=sic-] *)`.
   Verified live at 45%: the tabs panel computes `rgba(25,25,31,0.45)` (was 0.7), so the stats panel, settings
   tabs and every other tabs surface now follow the Panel opacity slider.
70. **Info page replaced with the admin's text (2.46.0)** - "In the info panel remove everything then allow me to
   add my own info in the webapp." New config `replaceInfoPanel` + `infoPanelText` (multiline, web editor). The
   stylesheet hides the launcher's own link cards (`.info-card-height{display:none!important}`) and styles the
   injected block (`#clbi{white-space:pre-wrap;line-height:1.5}`); the app.js script injects the block on the
   Info page and fills it from the new `GET /info` endpoint of the rotation server, so text edits apply on
   Save & apply without re-patching the launcher. The background veil moved from an app.js script into the
   stylesheet (it is a pure CSS effect: `.background-overlay{background-color:rgba(0,0,0,{veilAlpha})!important}`),
   freeing that slot for the info script. Byte budget: the script needed 266 bytes; the trims were the 127.1 URL
   form, `a.after()`, id + CSS hook, optional chaining, `1e3` and dropping the marker (the script self-guards).
   Verified: "info panel replaced with the configured text" on both launchers, and on /Info the seven built-in
   cards compute display:none while the injected block shows the /info text.
71. **Info-page fix (2.46.1)** - reported: "It added the text to the profile screen not the info tab bottom left".
   The script had no page check and the CSS hid `.info-card-height` globally, so the profile screen (which shows
   the same cards) was hit. Now: the script returns unless `location.pathname[1]=='I'` and it REUSES the first of
   the launcher's own cards as the text block (`a.id='clbi'; a.innerHTML=''`), which also keeps the script at 255
   of app.js's 266 bytes; the CSS hides only the other cards and only while the block exists
   (`body:has(#clbi) .info-card-height:not(#clbi){display:none!important}`, `#clbi{white-space:pre-wrap;
   line-height:1.5;height:auto!important;pointer-events:none}`). The text is fetched from `GET /i`
   (the same handler also answers `/info`).
   Verified: `/Info` reuses the card (id=clbi) with the configured text while the other six cards compute
   display:none; other pages - including wherever the profile screen's cards are - are untouched.
72. **Exact Info-page gate (2.46.2)** - the reporter still saw the text on the Profiles screen: their launcher
   had not been re-patched (the screenshot is the 2.46.0 plain-text block, and re-patching needs the launcher
   closed), plus the gate was only `location.pathname[1]=='I'`. It is now exact:
   `if(location.pathname!='/Info')return;` - strictly safer (no other route can match) and 5 bytes smaller.
   Verified: `/` shows no injected block, `/Info` shows the reused card with the configured text and the other
   six cards hidden.
73. **Info card pick is image-safe (2.46.3)** - reported: "Icon showing on profile info" (the launcher's own
   server/profile card rendered with a giant broken image). 2.46.2 reused the FIRST `.info-card-height` on /Info,
   which on the reporter's build is that card - clearing its content and forcing `height:auto` wrecked the
   artwork inside it. The script now picks only `.info-card-height:not(:has(img))`, so a card holding an image
   is never touched (and `innerHTML=''` is gone: `textContent` replaces the card's text in one step). Dropping
   the extra `:not(img)` guard keeps the script at 257 bytes so it still fits app.js's 266 (604 of 607).
   Verified: on /Info six of seven cards compute display:none and the picked card (a DIV, `:has(img)` false)
   carries the configured text; both launchers report "info panel replaced with the configured text".
74. **Launcher artwork images hidden (2.46.4)** - reported: "It's still displaying the images in this folder:
   SPT_Runtime\SPT_Data\images\launcher" (side_bear/usec/scav/server, shown as a giant broken artwork).
   Root cause: 2.44.0's `StripDecorativeArtwork` removed the rules that positioned these `<img>`s and set
   `opacity:2%`, so they rendered at natural size, full opacity - the "giant icon" on the profile card.
   Fix: the stylesheet now hides them outright -
   `[class*=background-image-]:not(.background-image-cover){display:none!important}` - while keeping the
   rotation background (`background-image-cover`) visible. The rule costs 79 of the 136 free CSS bytes.
   Verified live: on `/`, `/Profiles`, `/Profile`, `/Settings`, `/Info` no image from `SPT_Data/images/launcher`
   is rendered (no `/images/side_*` in the DOM, zero visible decorative elements) and the profile card draws
   cleanly over the rotation background; an injected probe confirms decorative -> `display:none`,
   cover -> `inline`.
75. **Custom Info-panel link (2.47.0)** - "make it when you click on the text in info I can add a custom link
   in the webapp it uses instead of sp-mod.com." New config `infoPanelLink` (web editor, next to the info text);
   the rotation server serves it at `GET /l` (live - no re-patch needed to change it). A second script
   (`ApplyInfoPanelLink`, baked into navFade.js - the one slot with a spare kilobyte, so the tight app.js slot is
   untouched) fetches /l every 5 s and intercepts clicks in the CAPTURE phase at window level, which is what
   makes it beat the reused card's own Blazor handler. Details that mattered:
   - the reused card is wrapped in the stock `<a href="https://sp-mod.com/">`, so a real click's target is that
     anchor, not the card - the handler checks `t.closest('#clbi') || t.querySelector('#clbi')`;
   - the card's `#clbi` CSS had `pointer-events:none` (from the earlier inert-text fix), which let real clicks
     fall through to the anchor; it is now `cursor:pointer` so the card itself receives them.
   Verified with a real CDP mouse click at the block's coordinates: `opened: ["https://discord.gg/myserver"]`
   and the page stays on /Info (no sp-mod.com). Empty link = the text is inert (link suppressed either way).
76. **Multiple Info boxes, all linked (2.48.0)** - "make it have more than one window similar to how the
   launcher was originally, so I can have one box for some text then another box for other text... all boxes if
   clicked use the custom link". New config `infoPanelBoxes` (list of strings, edited as a dynamic list in the
   web editor with Add box / per-box delete); `infoPanelText` stays as the legacy fallback (first box).
   The server serves boxes + link in one payload at `GET /b` (JSON `{t:[...],l:"..."}`), still live.
   The single combined script (in navFade.js, ~745 of 1152 free bytes) now:
   - assigns box i to the i-th `.info-card-height:not(:has(img))` card via a shared `clbi` class,
     hides surplus cards (`body:has(.clbi) .info-card-height:not(.clbi)`), and clears stale classes;
   - intercepts clicks on ANY `.clbi` at window capture and opens the configured link (all boxes share it).
   The old single-text script that lived in app.js is detected (by the `clbi` marker) and restored to pristine
   content, so exactly one info script ever runs.
   Verified live with 3 boxes: cards 3/7 shown, 4 hidden, and real CDP mouse clicks on each box opened
   `https://discord.gg/myserver` while the page stayed on /Info.
77. **Info-box editor always visible (2.48.1)** - the box editor only rendered while "Replace the launcher's
   Info page" was ON, so a fresh config (or a switch left off) made it look like the feature was missing.
   The boxes and the link field are now always shown, greyed out (Disabled) while the switch is off, and the
   editor always starts with at least one box (falls back to the legacy infoPanelText). Verified by fetching
   the served page with the switch OFF (fields render disabled) and with it ON (one editable box + link field).
78. **No ghost click zones on the Info page (2.48.2)** - reported: the original cards were invisible but the
   spots they used to occupy were still clickable. Hiding `.info-card-height` alone left the wrapper anchors and
   their `.mud-grid-item` cells in place, so the old card areas kept receiving clicks.
   Now the stylesheet hides the whole grid cell of every box-less card
   (`.clb-on .info-card-height:not(.clbi),.clb-on .mud-grid-item:has(.info-card-height:not(.clbi)){display:none}`),
   and the marker class `clb-on` is added to the body by the Info script only while on /Info (removed as soon as
   the page changes, so other pages keep their cards). The script's check runs before the /b fetch, so cleanup
   also happens if the server is unreachable; the poll is 2 s so the class never lingers.
   Verified live: on /Info 3 grid cells shown / 4 hidden (elementFromPoint over the old spots now resolves to the
   page background, cursor auto, clicks do nothing), boxes still open the link; on other pages `clb-on` is
   absent and their cards stay visible.
79. **In-game prompt fix: server address parsing (2.48.3)** - the update prompt stopped appearing because
   the plugin could not read the launcher's own argument. SPT passes `-config={'BackendUrl':'https://host:port',
   'Version':'live',...}` - the key is *quoted*, and the old pattern (`BackendUrl\s*:\s*['"]...`) only matched an
   unquoted key, so the resolved origin silently fell back to `https://127.0.0.1:6969`. That used to work while
   the server bound loopback; after the server moved to the Tailscale address (fika-fixes `server_ip.json`)
   every fetch was refused and the plugin logged "Server does not offer a launcher background." (no request ever
   reached the server - the logs confirmed it).
   Now:
   - `BackendUrlProvider.GetCandidates()` scans the RAW command line first (this also covers a split
     "-config=" + "{...}" and double quotes), then the individual arguments, and appends the 127.0.0.1 /
     localhost fallbacks; quotes around the key and the value are optional in the pattern.
   - the check probes the candidates in order (20 s timeout) and keeps the first origin that answers, so the
     download and the staged update flow all use the working origin.
   - failures are logged with the tried origins and reasons ("Tried: origin (error); ...") instead of silence.
   Verified: harness self-tests pass (`LauncherUpdaterTest backend` -> 6 PASS; `decide` -> 9 PASS) and a live run
   of the exact plugin source, launched with the game's own command line, resolved `https://100.127.48.41:6969`
   and fetched the info from the running server (image 28-bg.png, build f6607c56...).
80. **Raid stutter fix: no more per-second object scans (2.48.4)** - reported as "constant stutter every
   second in raid". Cause: `PromptController.Update()` polled `FindObjectOfType<MenuUI>()` every second in
   *every* phase, forever - a full scan of all loaded Unity objects. With the prompt still unanswered (phase
   Prompt) that scan ran inside raids too, hitching the game once per second.
   Now the per-second menu tracking only runs while it is needed (`WaitingForMenu` or `Prompt`):
   - `WaitingForMenu` keeps the FindObjectOfType check (it verifies the menu is really up; bounded - the
     phase ends within ~30 s) and caches the found `MenuUI` for cheap liveness checks.
   - `Prompt` never scans: `MenuVisible()` is the cached object's liveness + `activeInHierarchy`, or the
     active scene name (EFT's menu scene is `MenuUIScene`, verified in the game's build settings).
   - other phases poll nothing at all.
   The "Main menu detected" log line now includes the active scene name for diagnostics.
81. **Packaging** - `dotnet build -c Release` produces
   `release/PreyToLive-CustomLauncherBackgrounds-<version>-SPTushonka-5.0.zip` with the
   `SPT_Runtime/user/mods/PreyToLive-CustomLauncherBackgrounds/` layout (every file listed explicitly, no
   build-junk pickup), and optionally deploys straight into an install when `-p:TarkovDir="C:\path\to\SPT\"`.

## Building

1. (Once, or after changes to the patcher / shared payload code) publish the remote patcher into `wwwroot`:
   run `build-patcher.cmd` (two `dotnet publish` runs: win-x64 + linux-x64, framework-dependent single file).
2. `dotnet build -c Release` - produces `release/PreyToLive-CustomLauncherBackgrounds-<version>-SPTushonka-5.0.zip`.
   The packaging step warns when the patcher binaries have not been published beforehand.

## Files

- `ModMetadata.cs` - GUID/name/version/SIC page registration.
- `ModConfig.cs`, `ConfigRegistration.cs`, `ConfigProvider.cs` - config load/save + web config editor.
- `BackgroundSelectionService.cs` - random pick + `ImageRouter` override + launcher patch + page state.
- `BackgroundOnLoad.cs` - `IOnLoad` hook (PostLoad).
- `LauncherBundle.cs` - single-file bundle entry + CLI resource locator (SRM based).
- `LauncherPayload.cs` - shared payload logic (padding/COM marker, backup + in-place swap + verify,
  dual-region bg.jpg + app.js swaps); also compiled into the standalone patcher.
- `LauncherRotationServer.cs` - tiny plain-HTTP rotation server (TcpListener) the patched launcher polls.
- `MediaLibrary.cs` - media list/import/delete for the web editor (numbered naming, path-safe deletes).
- `MediaFormatter.cs` - image formatting (PNG/JPEG, resize/crop) + mp4 container check (Windows-only image work).
- `NaturalOrder.cs` - natural file-name ordering shared by the playlist and the media lists.
- `ModSyncIntegration.cs` - registers the patched launcher as a Corter-ModSync served path.
- `LauncherPatcher.cs` - local patching: JPEG fitting via System.Drawing, per-file application.
- `patcher/` - `CustomLauncherBackgrounds.Patcher` standalone tool for remote players (net10.0 console app).
- `wwwroot/LauncherPatcher/` - published patcher binaries (win/linux), `patch-and-launch.cmd` and readme;
  served by the SIC and linked on the mod page. (Rebuild with the publish commands in PORT_NOTES/build step.)
- `wwwroot/InGamePrompt/` - the built client plugin (`CustomLauncherBackgrounds.Gui.dll`) + readme,
  downloadable from the mod page and staged into `BepInEx\plugins\` in the package (build with
  `build-client-plugin.cmd`).
- `client-plugin/` - the client plugin project (BepInEx 6 IL2CPP): `Plugin.cs`, `PromptController.cs`,
  `BackendUrlProvider.cs`, `LauncherExeUpdater.cs` (pure .NET update logic, shared with
  `client-plugin/tests/LauncherUpdaterTest` harness). Refs are vendored locally in `client-plugin/Refs/`
  (gitignored) from a live SPT 5.0 install - do not build against the older plain-managed client-refs.zip.
- `Web/Pages/Backgrounds.razor` - the SIC page.
- `res/Images/1-bg.png` ... `35-bg.png` - the original 35 background artworks as PNG (default playlist);
  `res/Videos/` is the folder for `.mp4` clips. Subfolders (`res/<name>`, legacy `res/launchers/<name>`)
  are still supported.
- `config.json` - shipped defaults.
- Shipped runtime deps (not part of the server runtime): `System.Drawing.Common.dll`,
  `System.Private.Windows.Core.dll`, `System.Private.Windows.GdiPlus.dll`, `Microsoft.Win32.SystemEvents.dll`,
  `System.Formats.Nrbf.dll` (all MIT, from the .NET runtime).

## Verification (SPT 5.0.0 BLEEDINGEDGEMODS-20260914, commit `ec15a40`)

Tested on a live server (full `SPT_Runtime` copy of the build, mod installed into
`user/mods/PreyToLive-CustomLauncherBackgrounds/`):

- Mod loads cleanly; a different random image is picked on every server start
  (e.g. `eft31.jpg` -> `eft17.jpg` -> `eft08.jpg` -> `eft07.jpg` -> `eft24.jpg` -> `eft01.jpg`; 35 images found).
- `GET /files/launcher/bg` -> HTTP 200, `image/jpeg`; served bytes identical to the chosen source file.
- `GET /launcher/v2/mod-pages` -> the mod page is listed for the launcher.
- `GET /custom-launcher-backgrounds` (SIC page) -> HTTP 200; live preview, usage notes and launcher patch
  status ("SPT.Launcher.exe: applied - applied eftXX.jpg ..."); mod card on the SIC landing page.
- Config editor: this mod shows as `FOUND, registered=True, file=config.json`; snapshot reads OK.
- **Launcher patching** (the important one):
  - After a server start, `SPT.Launcher.exe` (`SPT.Launcher.dll` entry) and `SPT.Launcher.Linux`
    (`SPT.Launcher.Linux.dll` entry) both contain the padded image with the `CLB-BG-PATCH:<file>` marker at
    the exact original resource offset; the 4-byte resource length prefix still reads 133100.
  - Backups `*.original` match the pristine files byte-for-byte (sha256).
  - **Ran the patched launcher and verified via the WebView2 debug protocol** that it serves/renders the new
    image: `img.background-image-cover` loads from `http://localhost/images/bg.jpg`, natural width 900
    (the artwork's actual width), and the fetched bytes hash (`sha256`) is byte-identical to the region
    written into the exe. A screenshot of the running launcher shows the new random background.
  - Re-apply on an already-patched exe works (marker detected, backup NOT refreshed; new image written).
  - Update simulation (pristine launcher restored): backup is refreshed from the pristine file and the new
    image is applied.
  - Lock test (launcher running while the server starts): the write fails gracefully with
    `SPT.Launcher.exe - could not write (Access to the path is denied.) - if the launcher is running, close it
    and restart the server`; the existing patched file is left untouched, and the unlocked
    `SPT.Launcher.Linux` is still updated.
- **Remote patching (2.2.0)**, simulated with a separate `client-test\SPT_Runtime` folder:
  - Server log: `remote launcher patch payload ready: /files/launcher/bg-fitted`; `GET /files/launcher/bg-fitted`
    returns HTTP 200 `image/jpeg`, 133100 bytes, **byte-identical to the region the server patched into its
    local launcher** (same sha256).
  - Patcher downloads work from the SIC (`/PreyToLive-CustomLauncherBackgrounds/LauncherPatcher/...`,
    README + cmd + Windows exe verified byte-identical to the published files).
  - Running the patcher against the client folder: auto-detected the server from `LauncherSettings.json`,
    downloaded `eft20.jpg` and patched both launcher files; regions byte-identical to the server payload,
    backups byte-identical to pristine. Re-running works (re-apply).
  - Negative test (server stopped): clear error and exit code 1.
  - **Launched the remotely patched client launcher and verified via the WebView2 debug protocol** that it
    serves/renders the new image (fetched sha256 == patched region == server payload; screenshot shows the
    new background with the "Remote Test" server card).
- **In-game launcher update (2.3.0)**, verified against the live test server + a separate client folder:
  - `GET /files/launcher/clb-info` returns the info JSON; `GET /files/launcher/clb-launcher` returns the full
    15,700,182-byte patched launcher whose sha256 matches the info, is byte-identical to the server's own
    patched `SPT.Launcher.exe`, and carries the marker.
  - The `LauncherUpdaterTest` harness (runs the exact `LauncherExeUpdater` code the plugin uses) fetched the
    info, downloaded the exe with progress, validated it, backed up the pristine client launcher, installed
    it and verified (client launcher == served exe, backup == pristine, no leftovers).
  - The installed client launcher was launched and verified via the WebView2 debug protocol to render the
    new background (served sha256 matches; screenshot).
  - All mod-page downloads verified over HTTP (plugin/linux/cmd via octet-stream `.bin` routes; exe + readme
    via static files).
  - Note: the IL2CPP plugin compiles cleanly against the interop refs, but the actual on-screen window
    cannot be exercised here (no game client is installed in this environment) - it should be loaded and
    seen on the live game. The non-UI logic is fully covered by the harness.
- **2.4.0 (close-the-game flow + ModSync), tested with the real Corter-ModSync components (v0.14.2):**
  - Server: installed `ModSync.Server.dll` + config into the test server, plus its required bootstrap files
    (`../ModSync.Updater.exe`, `../BepInEx/plugins/Corter-ModSync/Corter-ModSync.dll`) - without those,
    ModSync refuses to register its listener (learned the hard way). First start: our mod added the entry
    (config backup created, `SPT.Launcher.exe` count 0 -> 1). Second start: `.modsync-served.json` now lists
    `../SPT_Runtime/SPT.Launcher.exe | restartRequired: True | headless: False` and the server logs
    "Listening on /modsync/*".
  - API: `GET /modsync/hashes` lists `SPT_Runtime\SPT.Launcher.exe` with a hash; 
    `GET /modsync/fetch/SPT_Runtime/SPT.Launcher.exe` returns the 15,700,182-byte patched launcher
    **byte-identical** to the server's own file (marker `eft07.jpg`).
  - Client staging simulation (exactly what the plugin does): created the `.modsync-bak`, staged the
    fetched bytes into `ModSync_Data/PendingUpdates/SPT_Runtime/SPT.Launcher.exe`, started a dummy game
    process and ran the **real** `ModSync.Updater.exe --silent <pid>`: it logged waits, then "Copying file:
    ...SPT.Launcher.exe", applied it, deleted PendingUpdates and the client launcher became byte-identical
    to the staged file; the `.modsync-bak` was left in place.
  - Fallback path: `CustomLauncherBackgrounds.Patcher.exe --apply-staged <launcher> <staged> --wait-pid <pid>`
    was run against a dummy process: it waited (16 s), applied, created `.original` (== pristine),
    deleted the staged file and wrote `clb-launcher-update.log` with a clear timeline.
  - Also verified `--apply-staged` guards: missing files, slot mismatch, locked-file retry loop.
- **2.4.1 (close the launcher too), tested with a real running SPT.Launcher.exe:**
  - Confirmed the running launcher really locks its file (write handle -> "Permission denied").
  - Helper flow: with the launcher running and a dummy game process, `--apply-staged` waited for the game,
    logged `Closed running SPT launcher: SPT.Launcher (PID ...) (closed)` (graceful close), applied the
    staged launcher, created `.original`, deleted the staged file - and the launcher process was gone.
  - ModSync flow: with the launcher running, the real `ModSync.Updater.exe` could not apply (file unchanged,
    pending retained, "Error while attempting to copy updated files"); after closing the launcher the same
    updater run applied the staged file and cleaned PendingUpdates ("Done!"). This is exactly why the
    plugin now closes the launcher before the updater runs.
- **2.5.0 (background rotation), verified live against the real launcher:**
  - Extracted the launcher's embedded web assets and sized the injection: the rewritten `app.js` (original
    script loader, minified, plus rotator) is 440 bytes inside the 607-byte slot.
  - Cert test first: the launcher webview rejects https://127.0.0.1:6969 (fetch + img both fail) - which is
    why rotation uses the plain-HTTP helper.
  - Server run (test interval 5 s): both launcher files report `applied launcher script (607 bytes)`; the
    exe contains `CLB-ROTATE url=http://127.0.0.1:6970 interval=5000`; `curl http://127.0.0.1:6970/bg.jpg`
    twice returns different images; the SIC page shows `Background rotation: Every 5s on
    http://127.0.0.1:6970 (currently ...)`.
  - Launched the patched launcher and watched it: `img.background-image-cover` switched to
    `http://127.0.0.1:6970/bg.jpg?v=...`, natural width 900 (loaded), and two screenshots ~7 s apart show
    two different backgrounds (pixel-region hashes differ, visually confirmed).
  - Negative test: with the server stopped, the launcher keeps the last image (src unchanged after 8 s,
    still loaded) - no flicker, no broken image; rotation resumes when the server is back.
- **2.5.1 (re-prompt when the server launcher changes):**
  - `PromptDecider` unit tests (harness `decide` mode): 9/9 pass - neverAsk wins; fresh players prompted;
    updated players quiet on the same build and prompted on a new build (including the "updated with an
    older plugin" case); "Later" quiet for the same image/build and prompted for a new image or build.
  - Live build-id checks against the test server: two restarts with the same config produced the SAME
    build id (`4853128c...`) while the image changed per start; changing `rotationSeconds` 5 -> 7 changed
    the build (`d45d47e1...`) - i.e. players are re-prompted exactly when the server's launcher changed.
- **2.6.0 (videos + crossfade), verified live with a video+image test folder (rotation 5 s):**
  - Patch run on a launcher still carrying the 2.5.x rotator: `legacy app.js rotator restored to original |
    applied eft00.jpg ... | applied launcher script (navFade.js) (1940 bytes)`; the exe now contains
    `CLB-ROTATE v3` + `m('video')`, the app.js region is byte-identical to the pristine backup, and the
    navFade region starts with the stripped original code.
  - Rotation server: `/` reports `sample.mp4 (video)` / `eft00.jpg (image)`; alternating ticks return
    `bg.mp4` 200 (991,017 bytes = the mp4) and `bg.jpg` 200 (339,874 bytes = the padded image), the other
    route 404s - i.e. the client's try-image-then-video flow works as designed.
  - Launched the patched launcher (WebView2/CDP): the injected `<video>` played (`readyState` ok, current
    time 1.81 -> 5.52 over the window) with 12 samples of intermediate opacity during the fade-in
    (crossfade), `min/max` opacity 0/1; the overlay image showed the same behaviour (7 fade samples).
  - Screenshots 5 s apart show the alternation: image artwork -> **Big Buck Bunny video frame** -> image
    -> video, i.e. mp4 backgrounds really play in the launcher.
  - The `--apply-staged` helper carries all matching script slots (navFade.js/app.js) plus the background
    over to the player's launcher; the downloadable exe bakes the same script with the remote address.
- **2.6.1 (res folder layout), verified live:**
  - Default config (`launcherFolderToUse: ""`): the mod picks from `res` directly - the 35 items in the
    res root are used (no subfolder needed).
  - Subfolder: `res/videotest` (image + mp4) with `launcherFolderToUse: "videotest"` resolves to the new
    `res/<name>` location; the legacy `res/launchers/<name>` layout was verified by dropping a folder
    there and confirming it still resolves via the fallback.
  - The release package now ships `res/*.jpg` (no `res/launchers` tree) and the default `config.json`
    ships with an empty `launcherFolderToUse`.
- **2.7.0 (Images/Videos folders + ordered rotation), verified live:**
  - Assets: 35 JPEGs converted to real PNG (900x450) as `res/Images/1-bg.png` ... `35-bg.png` (19.4 MB),
    `res/Videos/README.txt` placeholder; the package ships the new tree.
  - Default config: log shows the res playlist picked from `res/Images` (35 items); the rotation server logs
    `rotating 35 item(s) in order ... starting at X-bg.png`.
  - Ordered rotation: with a short test interval the `/` endpoint reports `1-bg.png`, `2-bg.png`,
    `3-bg.png` ... in sequence (wrapping at the end) - i.e. the numbered files really rotate in order.
  - Subfolder and legacy (`res/launchers/<name>`) resolution re-verified after the refactor.
- **2.8.0 (web editor), verified with a unit-test harness + live page render:**
  - `MediaTest` harness (links the shipped sources): 13/13 pass - natural order; crop 1600x900 -> 900x450
    PNG; stretch to size; keep-size + JPEG; BMP -> PNG; real mp4 accepted / fake mp4 rejected; numbered
    imports (1-bg.png, 2-bg.mp4), next number, listing with pixel dims, traversal-safe delete rejection,
    delete. The harness caught a real stream-leak in `TryReadImageSize` (would have blocked deletes) -
    fixed and re-verified.
  - Live page render on the test server: settings card (all switches + rotation fields + Save & apply),
    media table containing the real files (1-bg.png row), file input, formatter selects
    (900x450/crop/PNG), delete buttons - HTTP 200, 56 KB page; server meanwhile picked 21-bg.png and is
    rotating 35 items in order.
  - Note: the interactive click-through (upload/delete/save buttons) needs a real browser - the handlers
    are thin wrappers around the harness-tested logic plus the already-verified `Apply()`.
- **2.8.1 (responsive rotation), diagnosed against the real install and verified live:**
  - Diagnosis of `C:\SPT-BLEEDINGEDGEMODS-5.0.0-47242`: launcher carried `//CLB-ROTATE v3` with baked
    `U=http://127.0.0.1:6970` and `M=300000` - correct patch, but poll cadence == rotation cadence (300 s),
    so drift hid changes for up to ~10 minutes.
  - Live test with the server rotating every 60 s: 80 s timeline sampled every 3 s - server states
    10-bg -> 11-bg -> 12-bg; the launcher swapped 3 s after each server tick (max lag 3.0 s, image always
    loaded). Video/image mix test (`res/videotest`, 20 s): state `v|sample.mp4` -> video layer visibly
    playing (opacity 1, bg.mp4) in 12 samples, `i|1-bg.png` -> image swaps + video fades to 0.
- **2.8.2 (rotation log gating), verified live:** with `rotationSeconds: 8` and `consoleLogs: false` the
  server log contained 0 per-rotation lines (only the startup line `rotating 35 item(s) in order every 8s`);
  with `consoleLogs: true` the per-step lines reappeared (`background rotation -> 6-bg.png (6/35)`).
- **2.8.4 (no-blur), verified live:** patch log shows
  `applied 21-bg.png ... | applied launcher script (navFade.js) (1940 bytes) | applied launcher script (backgrounds.css) (1148 bytes)`;
  the exe contains `blur(0px)` (background overlay) while `blur(10px)` (dialog backdrop) is untouched; a
  running launcher reports `backdrop-filter: blur(0px)` with the overlay tint `rgba(0,0,0,0.6)` unchanged,
  and a second server run keeps `blur(0px)` (idempotent rebuild from the pristine base). Screenshot
  comparison shows the artwork crisp instead of hazy.
- **2.9.0 (chrome removal), verified live:** the exe carries both override rules, `blur(0px)`, no
  `blur(2px)` and an untouched dialog `blur(10px)`; in the running launcher the computed styles are
  `mud-drawer` background transparent + border 0 + shadow none, `mud-button-filled-dark` background
  transparent/border 0, the minified stylesheet still parses (`img.background-image-cover` computes
  `object-fit: cover`) and all 6 rail icons are still present. Screenshot shows the artwork behind the
  rail icons and the floating top row, with the server card keeping its readable panel.
- **2.10.0 (borderless window), verified live:** a fresh launcher window was picked up by the server
  watcher within seconds (`launcher window made borderless` in the log), its caption bit cleared, the DWM
  border call returns S_OK and a pixel probe of the window edges shows artwork (no border line); with
  `launcherWindowBorderless: false` the title bar stays, no watcher lines appear and the mod page reports
  `Disabled via config.json`.
- **2.10.1 (native chrome + tint), verified live:** a fresh window kept its caption and its top edge pixel
  probed exactly #404048 (64,64,72) - the launcher's light shade - with artwork below; stripping the caption
  manually (a 2.10.0-style window) had the watcher restore the title bar within 8 seconds; the server log
  reports `watching for the launcher window (borderless: False, border tint: True)`.
- **2.10.2 (panel opacity), verified live:** after the patch the running launcher computes
  `.mud-paper` background = `rgba(25, 25, 31, 0.45)` (was 0.7) with the drawer still transparent, the exe
  contains the new rule exactly once, and a screenshot shows the artwork showing through the server card
  with the text still readable. The test also exercised the re-patch path with an already-patched base
  (no backup), which exposed the override-append overflow - fixed by stripping the previous override block
  first (log `applied launcher script (backgrounds.css) (1148 bytes)`, block count 1).
- **2.10.3 (route robustness), verified live:** `clb-info` returns valid JSON with patchLauncher on (200);
  overwriting the info file with `{}` while the route is live (the exact post-toggle state) returns
  `200 {}` instead of the old 500; a fresh run with patchLauncher off returns 404, writes `{}` to the cache,
  logs `remote launcher update withdrawn (launcher patching is disabled (patchLauncher: false))` and the
  server log contains no `Could not find file` errors.
- **2.11.0 (configurable opacity), verified live:** with `launcherPanelOpacityPercent: 20` the patch log
  shows `applied launcher script (backgrounds.css) (1148 bytes) - panels at 20%` and the running launcher
  computes `.mud-paper` = `rgba(25, 25, 31, 0.2)` (drawer still transparent); back at 45 the exe contains
  `rgba(25,25,31,0.45)!important` and the log reads `- panels at 45%`; the mod page renders the
  "Panel opacity %" field with its helper text.
- **2.11.1 (frost scaling), verified live:** opacity 0 -> cards compute `rgba(25,25,31,0)` + `blur(0px)`
  (screenshot: text and border over fully sharp artwork); opacity 45 -> `rgba(25, 25, 31, 0.45)` +
  `blur(13px)`.
- **2.12.0 (rotationHttpIP), verified live:** with `rotationHttpIP: 192.168.0.122` the rotation server bound
  both addresses (`/state` returned 200 on 127.0.0.1 and on the LAN IP), the downloadable
  `launcher-patched.exe` baked `http://192.168.0.122:6970` while the local `SPT.Launcher.exe` kept the
  `http://127.0.0.1:6970` script, and the SIC page showed `Background rotation: Every 10s on
  http://127.0.0.1:6970 (other computers: http://192.168.0.122:6970)`. A bogus `999.999.1.1` value logged
  `rotationHttpIP '999.999.1.1' is not a valid IP address` and fell back to loopback.
- **2.13.0 (toggles), verified live:** with both kinds on and a video present the /state kind sequence
  flipped `iivviiivvii` (rotations alternate image/image-video); images-only -> all `i`; videos-only ->
  all `v`; both off -> no rotation endpoint; the editor page renders the Images and Videos sections with
  their toggles. An early build of the round-robin placed all videos up front (invisible with many images,
  caught by the test) - the shipped interleave wraps the shorter list and truly alternates.
- **2.14.0 (video formatter), verified via the linked-sources harness + ffprobe:** with a portable ffmpeg the
  transcode produced a valid mp4 at exactly 900x450 (ffprobe: h264, 900, 450) with the note
  `formatted to 900x450`; with no ffmpeg the bytes are returned unchanged (`ffmpeg not found - video stored
  unchanged`); 'keep original size' returns the clip unchanged; the web page renders the Video size/fit
  selects, the ffmpeg path field and the upload button.
- **2.15.0 (per-kind numbering), verified via the linked-sources harness:** an empty media set imports the
  first image as `1-bg.png` and the first video as `1-bg.mp4`; following uploads become `2-bg.jpg` /
  `2-bg.mp4`; both per-kind counters report 3; kind detection checks (`.PNG`/`.webp` = image, `.MP4` =
  video, `.png` not a video) pass and the listing/deletion tests still pass.
- **2.16.0 (gap closing), verified via the harness + live:** harness - deleting `2-bg.png` from
  `1,2,3-bg.png + 4-bg.jpg + custom.png` closes to `1-bg.png, 2-bg.png, 3-bg.jpg, custom.png` (2 renamed),
  the videos folder is untouched and a second pass renames 0; live - deleting `5-bg.png` from the deployed
  34 images and restarting logged `renumbered 30 media file(s) to close numbering gaps` and left the folder
  contiguous 1..34.
- **2.17.0 (ordered start), verified live:** with `randomRotationStart: false`, images only, 4s interval,
  the served sequence was 10-bg -> 11-bg -> ... -> 18-bg (ascending by exactly 1 - number order confirmed),
  and with a 300s interval the very first served item was `i|1-bg.png` (ordered start); the default random
  mode started at `i|24-bg.png`.
- **2.18.0 (duplicate check), verified via the harness + page render:** an empty folder reports no
  duplicate; importing `1-bg.png` then the same bytes detects the duplicate; a same-length-but-one-byte-
  different image is NOT flagged; the mp4 duplicate check matches its own kind only; only 3 files exist
  after the sequence (no extra copies). The editor page still renders all sections with no server errors.
- **2.19.0 (replace override), verified via the harness:** `killa.png` -> `1-bg.png`; unchanged re-upload =
  duplicate; a changed `killa.png` replaces `1-bg.png` in place (one file, new bytes, same number);
  re-uploading the new version = duplicate; a different name with identical content is still a duplicate;
  the origin survives renumbering (video origin tracked through `Renumber`) and is dropped by `Delete`.
- **2.19.1 (prompt copy/look):** the plugin builds clean with the new reference; the exact strings
  (`Custom Launcher Backgrounds`, `The server has updated it's launcher since your last session.`,
  `Launcher is outdated`, `Update`, `Skip`, `Never`) are present in the built DLL; the layout/colours were
  previewed against the same virtual-unit geometry the plugin uses (640x300 at 1.5x).
- **2.20.0 (2 GB uploads), verified via the harness + live:** the file pipeline passes (`LooksLikeMp4File`,
  `HashFile` equals the byte hash, `ImportFromFile` -> `1-bg.png`, duplicate + replace from file, video
  import) and `FormatVideoFile` transcoded a clip to h264 900x450 (ffprobe) while the no-ffmpeg path
  returns the unchanged note; live, the streaming rotation server served `1-bg.mp4` (991 017 bytes) and
  `18-bg.png` (701 957 bytes) byte-identically to the files on disk, and the editor shows the 2 GB note.
- **2.20.1 (mod-folder ffmpeg), verified via the harness:** a dummy `ffmpeg\ffmpeg.exe` inside a temp mod
  directory is found with a blank config, a relative configured path (`ffmpeg\ffmpeg.exe`) resolves to it, an
  absolute configured path wins, and a directory with nothing anywhere returns null.
- **2.21.0 (audio + crossfade + hidden card), verified live via CDP on the patched launcher:** the patch
  log shows `applied launcher script (userTabset.js) (5399 bytes) (slot: userTabset.js)` with exactly one
  `//CLB-ROTATE` in the exe (the old navFade rotator was restored); two video layers exist, the front one
  plays with `muted === false`; forcing `document.hidden` mutes both layers and clearing it unmutes them;
  the video-only rotation produced states `v|1-bg.mp4` / `v|2-bg.mp4` with an overlapping crossfade state
  (both layers carrying media, one at opacity 1 and the other at 0); the `127.0.0.1:6969` card computes
  `display: none` (and a freshly cloned visible card was re-hidden within a second by the observer), and the
  screenshot shows the launcher without the card.
- **2.22.0 (mute button), verified live via CDP:** all four patch steps log (`userTabset.js`, `backgrounds.css`,
  `externalLinks.js` + the background swap) with exactly one `//CLB-ROTATE` and one `//CLB-MUTE` in the exe;
  the button sits at right/top 14 px, reads "Mute" when audible and "Unmute" when muted; clicking mutes the
  playing layers (`[true,true]`) and clicking again unmutes them (`[false,false]`); the state survives a page
  reload (`localStorage`); the overlay layers measure the full viewport (1054x521) after the `.clbov` fix;
  the full feature suite (audio, minimize-mute, video crossfade, hidden card) reports ALL CHECKS PASS.
- **2.23.0 (hide toggle), verified live both ways:** with the toggle on the exe bakes `H='127.0.0.1'` and the
  card computes `display: none`; flipping it off and re-patching brings the card back (`display: flex`); the
  editor page shows the new switch and no longer the old text field.
- **2.24.0 (rail mute tile), verified live via CDP:** the tile is `.nav-bottom`'s first child with the
  `mud-nav-link` class, computes `background-color: rgb(32, 32, 32)` (#202020), spans the rail width (56 x 42)
  and sits above Settings; clicking it stores `clbMute=1`, mutes both video layers and shows the slash;
  clicking again unmutes and hides it.
- **2.24.1 (clear mute tile), verified live:** the mute tile and the Settings/Info entries all compute
  `background-color: rgba(0, 0, 0, 0)` and `color: rgba(255, 255, 255, 0.498)`; clicking still stores the flag
  and mutes both video layers.
- **2.24.2 (icon), verified live:** unmuted shows exactly the two paths with the X path at `display: none`
  and the stroke inheriting the rail colour (`rgba(255,255,255,.498)`); muted sets the X path to visible and
  mutes both video layers; zoomed screenshots of both states confirm the arcs and the arcs+X.
- **2.24.3 (icon revert), verified live:** the tile computes `background-color: rgba(0,0,0,0)` with fill/colour
  `rgba(255,255,255,.498)`; unmuted hides the slash path (`display: none`) and shows the speaker; muted shows
  the slash and mutes both video layers; zoomed screenshots confirm both states.
- **2.24.4 (slot fix), verified live:** the hidden card's grid item now computes `display: none` with a
  0 x 0 rect (was 330 x 24); a simulated "add server" (an extra item+card appended to the grid) landed at
  x = 48 - the FIRST slot - instead of the second cell at x ~ 378; the mute tile still toggles with the
  shortened `cm` key (stores the flag, mutes both video layers); the patch log shows all four steps applied.
- **2.25.0 (volume), verified live via CDP:** the Settings page shows the Volume row inside the Interface
  paper (label "Volume", `input[type=range]`, full row width); setting it to 30/40/75 stored `cv` and the
  playing layers reported volume 0.3/0.4/0.75; the feature suite (now visibility-aware) reports the audio
  state correct, mute-on-hidden working and a video-to-video swap with an overlapping crossfade; the hidden
  card's grid cell stays `display: none` across 10/20/30 s checks.
- **2.26.0, verified:** harness - `DeleteMany` returns only the deleted names, removes exactly them, renumbers
  `1..5` -> `1,2,3` after deleting 2 and 4, and rejects invalid names; live - with images every 4 s / videos
  every 12 s the `/state` change-gaps alternate 4.0 and 12.0 by kind; the editor page renders the two rotation
  fields and the checkbox column (the bulk button appears once a file is selected).
- **2.27.0 (veil toggle), verified live both ways:** with the toggle off the exe gets the `//CLBB` script and
  `div.background-overlay` computes `display: none` while a `.mud-paper` still computes `backdrop-filter:
  blur(13px)` (side bars unaffected), and the mute tile + both video layers still work (proving the compacted
  app.js loader is intact); with the toggle on the script is gone (`//CLBB` count 0) and the overlay is back
  (`display: block`, `rgba(0, 0, 0, 0.6)`). Screenshot shows the artwork fully clear with the veil off.
- **2.28.0 (background opacity), verified live:** with opacity 60 and the veil on, the background container
  computes `opacity: 0.6` while a `.card-container` stays at `opacity: 1` and the veil stays `display: block`
  (the two tweaks compose); the screenshot shows the artwork dimmed with the UI crisp.
- **2.29.0 (prompt on apply), verified live:** run A published build `b3b4facc...`; a plain restart (no save)
  published the SAME build; after simulating a Save & apply (revision file -> 1) the build became
  `20ee4c7f...` - so players who applied the previous build are prompted again while restarts stay quiet.
- **2.30.0 (veil percent), verified live:** with 50 the veil layer computes `rgba(0, 0, 0, 0.3)` and with 0 it
  computes `rgba(0, 0, 0, 0)`; screenshots show the soft veil at 50 and the vivid, fully clear artwork at 0.
- **2.31.0 (border tint tied to panel opacity), verified live:** with panel opacity 45 the status reads
  "border tinted ... (panel opacity 45%)"; with 85 it reads "Disabled (borderless off and panel opacity 85%
  is at the stock look, so the border is left alone)". (The DWMWA border colour itself cannot be queried on
  the test machine - DwmGetWindowAttribute returns E_INVALIDARG there - so the decision text is the evidence.)
- **2.32.0 (image enhance), verified via the harness:** `EnhancePercentFor` maps off/light/strong ->
  0/35/70 (case-insensitive); enhanced output keeps the configured size (320x180); pixels change; edge energy
  rises monotonically (plain 2.45 -> light 2.77 -> strong 3.19) and colour spread grows (40.97 -> 48.29);
  the editor page renders the dropdown with its Off/Light/Strong options.
- **2.33.0, verified:** harness - keep-size enhance (the re-apply path) preserves dimensions (320x180) and
  extension (.png) while sharpening (edge energy 2.45 -> 2.77); package - both DLLs stamp 2.33.0, the shipped
  config.json defaults to `imageEnhance: light`; live - the editor renders the dropdown, the "Re-apply enhance
  to stored images" button and the updated helper text.
- **2.33.1, verified:** the packaged DLL contains the new button text/caption and `ReenhanceImages`; the live
  editor page renders "Save & re-apply enhance to stored images" plus the "saves the choice above first"
  caption; both DLLs stamp 2.33.1.
- **2.34.0 (luminance sharpening), verified via the harness:** edge energy 2.45 (off) -> 4.22 (light) -> 4.60
  (strong) on the test image - previously only 2.77 / 3.19 - while the colour spread stays put (40.97 off vs
  40.58 strong, was 48.29 before); the keep-size re-apply path measures the same (2.45 -> 4.22); sizes and
  extensions are preserved.
- **2.35.0 (enhance removed), verified:** `grep` shows no remaining `ImageEnhance` / `EnhancePercent` /
  `EnhanceAmount` / `Reenhance` / `LumaUnsharpMask` references in the mod sources, the packaged config.json has
  no `imageEnhance` key, the editor no longer renders the dropdown or the re-apply button, and the media
  harness (with its two enhance test blocks removed) still reports ALL MEDIA TESTS PASSED.
- **2.36.0, verified via the harness:** fit mode outputs the target size (1920x1080) and keeps all four
  distinctive corners of a square test source (nothing cut), while crop mode loses them - reproducing the
  reported problem - and the new option strings build into the page.
- **2.38.0, verified:** harness grain test passes (speckle 22.97 -> 12.53, hard-edge contrast 255 -> 255) alongside the
  earlier deblock checks; the editor renders the new "Maximum" entry.
- **2.39.0, verified via the harness:** `DetectImageFormat` recognises JPEG/PNG bytes and returns null for unknown
  data; a JPEG named `.png` is stored as `.jpg` and a PNG named `.jpg` as `.png` under the auto setting, while
  explicit PNG/JPEG choices still win.
- **2.40.0, verified live:** the flat-image screen capture measures spatial stddev 0.00 (was 1.35) in both
  sampled areas with the stock veil; a live rotation over 35 images + 2 videos shows the canvas at opacity 1
  while a still is current and 0 while a video plays, videos audible/unmuted, crossfade and hide-card intact.
- **2.41.0, verified live:** polling across an image/video rotation shows the video layers as paused [true,true]
  and muted [true,true] whenever a still is current, and the front layer playing with muted false when a video
  is current.
- **2.42.0, verified live:** around each still change the canvas opacity dips 1 -> 0 -> 1, the base img src is
  swapped to the incoming image at the same moment, and a mid-transition screen capture is a 50/50 double
  exposure of the two artworks.
- **2.43.0, verified:** the plugin builds clean; the packaged Gui.dll contains MakeRoundedTexture /
  SecondaryButtonHeight; a preview render at the plugin geometry matches the reference design.
- **2.44.0, verified live:** injected probes compute rgba(25,25,31,0.45) for `.sic-panel` and
  `.mod-pages-panel`, rgba(0,0,0,0) for a `sic-` element nested inside one, and the existing `.mud-paper`
  cards still compute 0.45; the deployed stylesheet carries the new selectors with zero decorative rules left.
- **2.45.0, verified live:** every `.mud-tabs` surface computes `rgba(25,25,31,0.45)` at 45% (was 0.7 for the
  character stats panel); `.mud-paper` cards still 0.45.
- **2.46.0, verified live:** /Info shows only the configured text (7 built-in cards display:none, injected block
  present with the /info content); both launcher patches report "info panel replaced with the configured text".
- **2.4.2 (prompt UX):** the window is now a large, centered panel: 640x300 "virtual" units rendered at
  1.5x through `GUI.matrix` (text and buttons ~50% larger on every resolution), centered on the screen,
  server URL removed, clearer copy, full-width row of properly sized buttons, small word-wrap helper for
  error messages. The prompt stays away once the player has either updated successfully (`LauncherUpdated`)
  or chosen "Don't ask again" (`NeverAsk`); both are plugin config flags and can be set back to false to
  be asked again. Compile-verified only - there is no game client in this environment to render IL2CPP
  OnGUI; the layout math was checked against the available width (button row 552 of 584 units).
