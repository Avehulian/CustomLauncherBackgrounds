using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.Versioning;
using System.Text;
using SPTarkov.Common.Models.Logging;
using Encoder = System.Drawing.Imaging.Encoder;

namespace PreyToLive.CustomLauncherBackgrounds;

/// <summary>
/// Writes the chosen background image directly into the launcher executables that sit next to the server
/// (SPT.Launcher.exe / SPT.Launcher.Linux) by replacing the embedded wwwroot/images/bg.jpg resource bytes
/// in place at the exact same length (see <see cref="LauncherPayload"/>). A one-time backup is kept next to
/// the launcher. For launchers on other computers the same payload is served to <c>CustomLauncherBackgrounds.Patcher</c>.
/// </summary>
internal static class LauncherPatcher
{
    private static ISptLogger<BackgroundSelectionService>? _logger;
    
    internal static void SetLogger(ISptLogger<BackgroundSelectionService> logger) => _logger = logger;

    /// <summary>Slot size used for the remote payload when no local launcher is available (SPT 5.0 launcher).</summary>
    public const int DefaultSlotSize = 133100;

    private static readonly string[] LauncherFileNames = ["SPT.Launcher.exe", "SPT.Launcher.Linux"];

    internal sealed record PatchResult(string FileName, bool Applied, string Detail);

    internal static IReadOnlyList<PatchResult> ApplyToRuntime(string runtimeDirectory, string? staticImagePath, string? rotationUrl, int rotationIntervalMilliseconds, int panelOpacityPercent, string? hideServerAddress = null, int backgroundVeilPercent = 100, bool replaceInfoPanel = false)
    {
        var results = new List<PatchResult>();

        foreach (var fileName in LauncherFileNames)
        {
            var path = Path.Combine(runtimeDirectory, fileName);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var located = LauncherPayload.LoadAndLocate(path);
                var backupPath = path + ".original";
                var details = new List<string>();

                // 2.5.x carried the rotator in app.js - restore the pristine script so two rotators never fight.
                var cleanup = RestoreLegacyAppJsRotator(located, backupPath);
                if (cleanup is not null)
                {
                    details.Add(cleanup);
                }

                if (staticImagePath is null)
                {
                    details.Add("embedded fallback image kept (no images in the active folder)");
                }
                else
                {
                    var source = File.ReadAllBytes(staticImagePath);
                    try
                    {
                        var fitted = FitToSlot(source, located.Region.DataLength)
                            ?? throw new LauncherPayload.LauncherPatchException($"could not re-encode {Path.GetFileName(staticImagePath)} to fit {located.Region.DataLength} bytes - skipped");
                        var padded = LauncherPayload.PadToExactSize(fitted, located.Region.DataLength, LauncherPayload.Marker + Path.GetFileName(staticImagePath));
                        details.Add($"applied {Path.GetFileName(staticImagePath)} {LauncherPayload.SwapInPlace(path, located, padded)}");
                    }
                    catch (Exception ex) when (ex.GetType().FullName?.Contains("FileNotFoundException") == true && ex.Message.Contains("System.Drawing"))
                    {
                        // System.Drawing not available on this runtime (e.g., .NET Framework), skip image fitting
                        details.Add($"launcher background skipped (System.Drawing unavailable - rotation server handles backgrounds)");
                    }
                }

                if (rotationUrl is not null && rotationIntervalMilliseconds >= 1000)
                {
                    details.Add(ApplyRotationScript(path, located, backupPath, rotationUrl, rotationIntervalMilliseconds, hideServerAddress));
                }

                details.Add(ApplyStyleTweaks(path, located, backupPath, panelOpacityPercent, backgroundVeilPercent, replaceInfoPanel));
                details.Add(ApplyMuteButton(path, located, backupPath));
                details.Add(ApplyInfoPanel(path, located, backupPath, rotationUrl, replaceInfoPanel));

                results.Add(new PatchResult(fileName, true, string.Join(" | ", details)));
            }
            catch (LauncherPayload.LauncherPatchException ex)
            {
                results.Add(new PatchResult(fileName, false, ex.Message));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                results.Add(new PatchResult(fileName, false, $"could not write ({ex.Message}) - if the launcher is running, close it and restart the server"));
            }
            catch (Exception ex)
            {
                results.Add(new PatchResult(fileName, false, $"unexpected error: {ex.Message}"));
            }
        }

        return results;
    }

    /// <summary>
    /// Patches one of the launcher's embedded front-end scripts so its background polls the rotation
    /// endpoint (the launcher page is served over plain http://localhost, so it can load http media from
    /// the rotation server without any certificate trouble). Images crossfade and mp4 videos play.
    /// </summary>
    private static string ApplyRotationScript(string path, LauncherPayload.LocatedLauncher located, string backupPath, string rotationUrl, int rotationIntervalMilliseconds, string? hideServerAddress)
    {
        try
        {
            // The slot with the most free space wins (userTabset.js on current builds); navFade.js/app.js are
            // only used when they happen to be roomier.
            var slot = FindBestScriptSlot(located, backupPath, rotationUrl, hideServerAddress);
            if (slot is null)
            {
                return "rotation script skipped (no rotator slot with room available)";
            }

            var baseBytes = GetPristineScriptBase(located, backupPath, slot);
            if (baseBytes is null)
            {
                return "rotation script skipped (no clean script base available)";
            }

            var script = BuildScriptContent(baseBytes, slot.DataLength, rotationUrl, hideServerAddress);
            if (script is null)
            {
                return $"rotation script skipped (does not fit {slot.Name})";
            }

            var result = LauncherPayload.SwapScriptInPlace(path, located, slot, script);

            // Any other script slot that still carries an older rotator goes back to pristine content, so
            // exactly one rotator runs (and the old slot stops wasting space).
            foreach (var other in located.Scripts)
            {
                if (!other.Name.EndsWith(".js", StringComparison.OrdinalIgnoreCase) || ReferenceEquals(other, slot))
                {
                    continue;
                }

                var current = Encoding.UTF8.GetString(located.FileBytes, other.DataOffset, other.DataLength);
                if (current.IndexOf("//CLB-ROTATE", StringComparison.Ordinal) < 0)
                {
                    continue;
                }

                var pristine = GetPristineScriptBase(located, backupPath, other);
                if (pristine is null)
                {
                    continue;
                }

                var restored = LauncherPayload.PadTextToExactSize(Encoding.UTF8.GetBytes(CompactScriptBase(pristine)), other.DataLength);
                LauncherPayload.SwapScriptInPlace(path, located, other, restored);
            }

            return result + $" (slot: {slot.Name})";
        }
        catch (Exception exception)
        {
            return "rotation script skipped: " + exception.Message;
        }
    }

    /// <summary>
    /// Chrome-free overrides appended to the launcher stylesheet (background art everywhere): the nav rail
    /// and the top bar's filled-dark button go transparent, and the frosted panels (the launcher's
    /// mudblazor.css paints them rgba(25,25,31,0.7)) drop to 45% so the artwork shows through more - the
    /// frosted blur stays. Appended after mudblazor.css in the cascade, so these later rules win.
    /// </summary>
    /// <summary>Start of the appended override block - used to strip previous runs before re-appending.</summary>
    internal const string OverrideMarker = ".mud-drawer{background-color:transparent";

    /// <summary>
    /// Builds the override block: transparent nav rail (icons stay), transparent top bar button, and the
    /// frosted panels at the configured opacity (0-100%; the launcher's stock value is 70, the mod's
    /// default is 45) with the frosting blur scaled to match (a fully clear setting really is clear).
    /// Value formatting is invariant and compact so the block always fits the stylesheet slot.
    /// </summary>
    internal static string BuildStyleOverrides(int panelOpacityPercent, int backgroundVeilPercent = 100, bool replaceInfoPanel = false)
    {
        var clamped = Math.Clamp(panelOpacityPercent, 0, 100);
        var alpha = (clamped / 100.0).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

        // The frosted blur fades with the fill, so 0% really is fully see-through (no tint, no frosting):
        // 0% -> blur(0px), 45% -> blur(13px), 70%+ -> the launcher's stock blur(20px).
        var blurPx = (int)Math.Round(20.0 * Math.Min(1.0, clamped / 70.0));
        var veilAlpha = (0.6 * Math.Clamp(backgroundVeilPercent, 0, 100) / 100.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

        return ".mud-drawer{background:transparent!important;box-shadow:none!important;border:0!important}"
            + ".clbov{position:fixed;width:100%;height:100%;object-fit:cover;pointer-events:none;opacity:0;transition:opacity 1s}"
            + ".mud-button-filled-dark{background:transparent!important;box-shadow:none!important;border:0!important}"
            + $".mud-paper,.mud-tabs,.mud-dialog,.mud-popover,.mod-pages-panel,[class*=sic-]:not([class*=sic-] *){{background-color:rgba(25,25,31,{alpha})!important;backdrop-filter:blur({blurPx}px)!important}}"
            // The background veil is a pure CSS effect, so it lives here instead of a script in app.js.
            + $".background-overlay{{background-color:rgba(0,0,0,{veilAlpha})!important}}"
            // Info page: hide the launcher's own link cards - the admin's text is injected by the app.js script.
            // The script turns one of the launcher's own cards into the text block (id=clbi) on the Info page
            // only; the rest of that page's cards are hidden while it is present.
            // Cards without a box are hidden together with their whole grid cell, so the spots the launcher's
            // own cards used to occupy are neither visible nor clickable. `clb-on` is set by the Info script on
            // /Info only, so other pages keep their cards.
            + (replaceInfoPanel ? ".clb-on .info-card-height:not(.clbi),.clb-on .mud-grid-item:has(.info-card-height:not(.clbi)){display:none!important}"
                + ".clbi{white-space:pre-wrap;line-height:1.5;height:auto!important;cursor:pointer}" : string.Empty)
            // The launcher's decorative corner artworks come from SPT_Data/images/launcher (bear/scav/usec/
            // server/settings). Their own positioning rules are stripped from the minified base, so without
            // this rule they would render at natural size and full opacity. Hide them, keep the rotation
            // background (background-image-cover) visible.
            + "[class*=background-image-]:not(.background-image-cover){display:none!important}";
    }

    /// <summary>
    /// Rebuilds the launcher's background stylesheet from its pristine version with the cosmetic tweaks:
    /// the backdrop blur in front of the artwork is zeroed (div.background-overlay uses blur(2px)), and
    /// overrides make the nav rail (panel, border, shadow) and the top bar's filled-dark button transparent
    /// so the artwork shows across the whole window while the rail icons stay. The stylesheet is minified to
    /// make room for the overrides, then padded to the exact slot size - repeated patches stay idempotent.
    /// </summary>
    private static string ApplyStyleTweaks(string path, LauncherPayload.LocatedLauncher located, string backupPath, int panelOpacityPercent, int backgroundVeilPercent, bool replaceInfoPanel)
    {
        try
        {
            var slot = located.FindScript("backgrounds.css");
            if (slot is null)
            {
                return "style tweaks skipped (this launcher build has no backgrounds.css)";
            }

            var baseBytes = GetPristineScriptBase(located, backupPath, slot);
            if (baseBytes is null)
            {
                return "style tweaks skipped (no clean stylesheet base available)";
            }

            var css = StripDecorativeArtwork(MinifyCss(Encoding.UTF8.GetString(baseBytes)));

            // Strip an override block from an earlier patch run (the pristine base may itself be an older
            // patched file, e.g. when the backup was created before a feature existed), so the result never
            // grows past the slot and the current overrides always win.
            var previous = css.IndexOf(OverrideMarker, StringComparison.Ordinal);
            if (previous >= 0)
            {
                css = css[..previous];
            }

            var sharpened = ZeroBackgroundOverlayBlur(css) ?? css;
            var opacity = Math.Clamp(panelOpacityPercent, 0, 100);
            var content = Encoding.UTF8.GetBytes(sharpened + BuildStyleOverrides(opacity, backgroundVeilPercent, replaceInfoPanel));

            if (content.Length > slot.DataLength)
            {
                return $"style tweaks skipped (do not fit {slot.Name})";
            }

            return LauncherPayload.SwapScriptInPlace(path, located, slot, content) + $" - panels at {opacity}%";
        }
        catch (Exception exception)
        {
            return "style tweaks skipped: " + exception.Message;
        }
    }

    /// <summary>
    /// Removes the four decorative corner artworks the launcher paints at 2% opacity (invisible in practice).
    /// They cost ~300 bytes of the style slot, which the panel/button overrides need instead.
    /// </summary>
    internal static string StripDecorativeArtwork(string css)
    {
        return System.Text.RegularExpressions.Regex.Replace(
            css,
            @"\.background-image-(?:bear|server|usec|settings)\{[^{}]*\}",
            string.Empty);
    }

    /// <summary>Conservative CSS minification: drop comments, collapse whitespace, trim around punctuation.</summary>
    internal static string MinifyCss(string css)
    {
        var withoutComments = System.Text.RegularExpressions.Regex.Replace(css, @"/\*.*?\*/", string.Empty, System.Text.RegularExpressions.RegexOptions.Singleline);
        var collapsed = System.Text.RegularExpressions.Regex.Replace(withoutComments, @"\s+", " ");
        var trimmed = System.Text.RegularExpressions.Regex.Replace(collapsed, @"\s*([{}:;,])\s*", "$1");
        return trimmed.Replace(";}", "}").Trim();
    }

    /// <summary>
    /// Zeroes the blur radius of the first <c>blur(...)</c> that belongs to the background overlay rule
    /// (keeping the string length identical, e.g. <c>blur(2px)</c> -> <c>blur(0px)</c>). Returns null when
    /// there is nothing to change.
    /// </summary>
    internal static string? ZeroBackgroundOverlayBlur(string css)
    {
        var anchor = css.IndexOf("background-overlay", StringComparison.OrdinalIgnoreCase);
        if (anchor < 0)
        {
            return null;
        }

        var blur = css.IndexOf("blur(", anchor, StringComparison.OrdinalIgnoreCase);
        if (blur < 0)
        {
            return null;
        }

        var digitsStart = blur + 5;
        var digitsEnd = digitsStart;
        while (digitsEnd < css.Length && char.IsDigit(css[digitsEnd]))
        {
            digitsEnd++;
        }

        if (digitsEnd == digitsStart)
        {
            return null;
        }

        var allZeroes = true;
        for (var i = digitsStart; i < digitsEnd; i++)
        {
            if (css[i] != '0')
            {
                allZeroes = false;
                break;
            }
        }

        if (allZeroes)
        {
            return null;
        }

        var chars = css.ToCharArray();
        for (var i = digitsStart; i < digitsEnd; i++)
        {
            chars[i] = '0';
        }

        return new string(chars);
    }

    /// <summary>Restores the pristine app.js when a 2.5.x rotator is still in it (it would fight the new one).</summary>
    private static string? RestoreLegacyAppJsRotator(LauncherPayload.LocatedLauncher located, string backupPath)
    {
        var slot = located.FindScript("app.js");
        if (slot is null || !LauncherPayload.RegionContainsText(located.FileBytes, slot.DataOffset, slot.DataLength, "CLB-ROTATE"))
        {
            return null;
        }

        try
        {
            if (!File.Exists(backupPath))
            {
                return "legacy app.js rotator left in place (no pristine backup to restore)";
            }

            var pristine = LauncherPayload.LoadAndLocate(backupPath);
            var pristineSlot = pristine.FindScript("app.js");
            if (pristineSlot is null || pristineSlot.DataLength != slot.DataLength)
            {
                return "legacy app.js rotator left in place (backup slot mismatch)";
            }

            Array.Copy(pristine.FileBytes, pristineSlot.DataOffset, located.FileBytes, slot.DataOffset, slot.DataLength);
            return "legacy app.js rotator restored to original";
        }
        catch (Exception exception)
        {
            return "legacy app.js rotator cleanup failed: " + exception.Message;
        }
    }

    /// <summary>The untouched script bytes: from the pristine backup when possible, else the current slot with an earlier rotator cut off.</summary>
    internal static byte[]? GetPristineScriptBase(LauncherPayload.LocatedLauncher located, string backupPath, LauncherPayload.ScriptSlot slot)
    {
        try
        {
            if (File.Exists(backupPath))
            {
                var pristine = LauncherPayload.LoadAndLocate(backupPath);
                var pristineSlot = pristine.FindScript(slot.Name);
                if (pristineSlot is not null && pristineSlot.DataLength == slot.DataLength)
                {
                    var bytes = new byte[pristineSlot.DataLength];
                    Array.Copy(pristine.FileBytes, pristineSlot.DataOffset, bytes, 0, bytes.Length);
                    return bytes;
                }
            }
        }
        catch
        {
            // fall through to the current slot
        }

        var current = new byte[slot.DataLength];
        Array.Copy(located.FileBytes, slot.DataOffset, current, 0, current.Length);
        return current;
    }

    /// <summary>
    /// Builds the replacement for a launcher script slot: the original script (comments and indentation
    /// stripped to free up room) plus the background rotation/crossfade rotator, padded with spaces to
    /// the exact slot size.
    /// </summary>
    internal static byte[]? BuildScriptContent(byte[] baseRegionBytes, int targetLength, string rotationUrl, string? hideServerAddress = null)
    {
        var content = Encoding.UTF8.GetBytes(CompactScriptBase(baseRegionBytes) + BuildRotatorScript(rotationUrl, hideServerAddress));
        if (content.Length > targetLength)
        {
            return null;
        }

        return LauncherPayload.PadTextToExactSize(content, targetLength);
    }

    /// <summary>
    /// The script base with an earlier rotator dropped (fallback path when no pristine backup exists) and
    /// comments/blank lines stripped, freeing as much room as the slot needs
    /// </summary>
    internal static string CompactScriptBase(byte[] baseRegionBytes)
    {
        var text = Encoding.UTF8.GetString(baseRegionBytes);

        // Drop anything a previous patch appended (rotator and/or mute tile) - both markers start with
        // "//CLB" - so re-patching starts from the pristine script and the slot never grows.
        var marker = text.IndexOf("//CLB", StringComparison.Ordinal);
        if (marker >= 0)
        {
            text = text[..marker];
        }

        var compact = new StringBuilder();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            compact.Append(line).Append('\n');
        }

        return compact.ToString();
    }

    /// <summary>
    /// Picks the script slot with the most free space for the rotator (userTabset.js on current builds,
    /// navFade.js/app.js fallbacks), using the pristine content of each slot.
    /// </summary>
    internal static LauncherPayload.ScriptSlot? FindBestScriptSlot(LauncherPayload.LocatedLauncher located, string backupPath, string rotationUrl, string? hideServerAddress)
    {
        var scriptLength = BuildRotatorScript(rotationUrl, hideServerAddress).Length;
        LauncherPayload.ScriptSlot? best = null;
        var bestFree = int.MinValue;

        foreach (var slot in located.Scripts)
        {
            if (!slot.Name.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var baseBytes = GetPristineScriptBase(located, backupPath, slot);
            if (baseBytes is null)
            {
                continue;
            }

            var free = slot.DataLength - CompactScriptBase(baseBytes).Length - scriptLength;
            if (free > bestFree)
            {
                best = slot;
                bestFree = free;
            }
        }

        return best;
    }

    /// <summary>
    /// The rotation script. Still images are drawn into a <canvas> rather than an <img>: Chromium dithers
    /// ("dot pattern") when it rasterises a large image layer, while the same pixels drawn through a canvas
    /// come out clean - measured on a flat test image: img = 1.35 spatial stddev, canvas = 0.00.
    /// Every 10 seconds it asks the rotation server for the tiny state string
    /// ("&lt;kind&gt;|&lt;name&gt;") and only downloads media when the background actually changed - so a
    /// rotation shows up within seconds of the server switching, while the launcher stays quiet (and cheap)
    /// in between. Images crossfade through an overlay image; videos play on two alternating layers so
    /// video changes crossfade exactly like images do, with sound that mutes while the window is minimized.
    /// When <paramref name="hideServerAddress"/> is set, server cards showing that address text are hidden.
    /// </summary>
    internal static string BuildRotatorScript(string url, string? hideServerAddress = null)
    {
        var hide = (hideServerAddress ?? string.Empty).Replace("'", string.Empty);

        return "//CLBR\n"
            + "(function(){var U='" + url + "',H='" + hide + "',V,W,C,N;\n"
            + "function g(){return document.querySelector('.background-image-cover')}\n"
            + "function m(t){var e=document.createElement(t);e.className='clbov';return e}\n"
            + "function p(){var i=g();if(!i)return;if(!V){V=m('video');W=m('video');[V,W].forEach(x=>{x.loop=1;x.playsInline=1;x.muted=1})}if(!C)C=m('canvas');var n=i.parentNode;if(V.parentNode!==n)n.insertBefore(V,i.nextSibling);if(W.parentNode!==n)n.insertBefore(W,V.nextSibling);if(C.parentNode!==n)n.insertBefore(C,W.nextSibling);return i}\n"
            + "function snd(){var u=document.hidden||localStorage.cm==='1'?1:0,v=+(localStorage.cv||100)/100;[V,W].forEach(x=>{if(x){x.muted=u;x.volume=v}})}\n"
            + "document.onvisibilitychange=snd;\n"
            + "document.addEventListener('cm',snd);\n"
            + "function img(x){var t=new Image;t.onload=function(){var j=g();if(j)j.src=x;[V,W].forEach(x=>{x.pause();x.muted=1;x.style.opacity=0});C.style.opacity=0;setTimeout(function(){C.width=t.width,C.height=t.height;C.getContext('2d').drawImage(t,0,0);C.style.opacity=1},1000)};t.src=x}\n"
            + "function vid(x){var o=V,n=W;V=n;W=o;o.muted=1;n.muted=1;C.style.opacity=0;n.oncanplay=function(){n.oncanplay=null;n.play().then(snd,function(){});n.style.opacity=1;o.style.opacity=0;setTimeout(function(){o.removeAttribute('src');o.load()},1200)};n.style.opacity=0;n.src=x}\n"
            + "function hide(){if(!H)return;var a=document.querySelectorAll('.card-container');for(var i=0;i<a.length;i++){var e=a[i];if(e.innerText&&e.innerText.indexOf(H)>=0){var z=e.parentElement;e=z?.className.indexOf('grid-i')>0?z:e;e.style.cssText+=';display:none!important' }}}\n"
            + "function vol(){var p=document.querySelectorAll('.mud-paper.mud-elevation-3')[1];if(!p||document.getElementById('clbv'))return;\n"
            + "p.insertAdjacentHTML('beforeend','<div class=\"d-flex gap-2\"><span>Volume</span><input id=\"clbv\" type=\"range\" style=\"flex:1\"></div>');\n"
            + "var s=document.getElementById('clbv');s.value=localStorage.getItem('cv')||100;s.oninput=()=>{localStorage.setItem('cv',s.value);snd()}}\n"
            + "if(window.MutationObserver)new MutationObserver(function(){hide();vol()}).observe(document.body,{childList:true,subtree:true});\n"
            + "function t(){hide();if(!p())return;var q='?v='+Date.now();fetch(U+'/state'+q).then(r=>r.text()).then(function(s){if(!s||s===N)return;N=s;s[0]==='v'?vid(U+'/bg.mp4'+q):img(U+'/bg.jpg'+q)})}\n"
            + "vol();t();setInterval(t,5000)})();\n";
    }

    /// <summary>
    /// Bakes the Info-page override: the stylesheet hides the launcher's own info cards (see
    /// BuildStyleOverrides) and this script fills one card per configured text box on /Info, each of which
    /// opens the configured link. Boxes and link are fetched live from /b, so both can be edited in the web
    /// editor without re-patching the launcher. It lives in the roomiest script slot (navFade.js) and any
    /// older copy left in another slot is restored to pristine content, so exactly one script runs.
    /// </summary>
    private static string ApplyInfoPanel(string path, LauncherPayload.LocatedLauncher located, string backupPath, string? rotationUrl, bool replaceInfoPanel)
    {
        try
        {
            var script = replaceInfoPanel && !string.IsNullOrWhiteSpace(rotationUrl)
                ? BuildInfoPanelScript(rotationUrl!)
                : string.Empty;

            LauncherPayload.ScriptSlot? chosen = null;
            if (script.Length > 0)
            {
                foreach (var name in new[] { "navFade.js", "app.js", "externalLinks.js", "navigateBack.js" })
                {
                    var candidate = located.FindScript(name);
                    if (candidate is not null && candidate.DataLength >= script.Length)
                    {
                        chosen = candidate;
                        break;
                    }
                }

                if (chosen is null)
                {
                    return "info panel skipped (no slot with room)";
                }

                var baseBytes = GetPristineScriptBase(located, backupPath, chosen);
                if (baseBytes is null)
                {
                    return "info panel skipped (no clean script base available)";
                }

                var content = Encoding.UTF8.GetBytes(CompactScriptBase(baseBytes) + script);
                if (content.Length > chosen.DataLength)
                {
                    return $"info panel skipped (does not fit {chosen.Name})";
                }

                LauncherPayload.SwapScriptInPlace(path, located, chosen, LauncherPayload.PadTextToExactSize(content, chosen.DataLength));
            }

            // Older builds kept an info script in app.js - restore any other slot that still carries one so
            // exactly one copy runs.
            foreach (var other in located.Scripts)
            {
                if (!other.Name.EndsWith(".js", StringComparison.OrdinalIgnoreCase) || ReferenceEquals(other, chosen))
                {
                    continue;
                }

                var current = Encoding.UTF8.GetString(located.FileBytes, other.DataOffset, other.DataLength);
                if (current.IndexOf("clbi", StringComparison.Ordinal) < 0)
                {
                    continue;
                }

                var pristine = GetPristineScriptBase(located, backupPath, other);
                if (pristine is null)
                {
                    continue;
                }

                var restored = LauncherPayload.PadTextToExactSize(Encoding.UTF8.GetBytes(CompactScriptBase(pristine)), other.DataLength);
                LauncherPayload.SwapScriptInPlace(path, located, other, restored);
            }

            return replaceInfoPanel ? "info boxes ready" : "info panel: stock";
        }
        catch (Exception exception)
        {
            return "info panel skipped: " + exception.Message;
        }
    }

    /// <summary>
    /// The Info-page script: on /Info it reuses the launcher's own link cards as text boxes - one card per
    /// configured entry, in order - and every box opens the configured link (capture phase, so the cards' own
    /// stock anchors never fire). Cards that are an image or contain one are skipped, and any card without a
    /// box is hidden by the stylesheet.
    /// </summary>
    internal static string BuildInfoPanelScript(string rotationUrl)
    {
        var baseUrl = rotationUrl.TrimEnd('/').Replace("://127.0.0.1:", "://127.1:");
        return "(function(){var B=[],U='';\n"
            + "function g(){if(location.pathname!='/Info'){document.body.classList.remove('clb-on');return}\n"
            + "fetch('" + baseUrl + "/b').then(r=>r.json()).then(function(j){B=j.t||[];U=j.l||'';\n"
            + "document.body.classList.add('clb-on');if(!B.length)B=[''];\n"
            + "var a=document.querySelectorAll('.info-card-height:not(:has(img))');\n"
            + "for(var i=0;i<a.length;i++){var c=a[i];if(i<B.length){if(!c.classList.contains('clbi'))c.classList.add('clbi');if(c.textContent!=B[i])c.textContent=B[i]}else c.classList.remove('clbi')}})}\n"
            + "window.addEventListener('click',function(e){var t=e.target;if(!t||!t.closest)return;"
            + "var c=t.closest('.clbi')||(t.querySelector?t.querySelector('.clbi'):null);if(!c)return;"
            + "e.preventDefault();e.stopImmediatePropagation();if(U&&window.openExternal)window.openExternal(U)},true);\n"
            + "g();setInterval(g,2e3)})();\n";
    }

    /// <summary>
    /// Adds the small Mute/Unmute button to the launcher's top right corner. It keeps its state in
    /// localStorage and notifies the rotator through a "clb-mute" event, so the two scripts stay
    /// independent. Lives in its own slot (externalLinks.js has room; the rotator uses the rest).
    /// </summary>
    private static string ApplyMuteButton(string path, LauncherPayload.LocatedLauncher located, string backupPath)
    {
        try
        {
            var script = BuildMuteButtonScript();
            LauncherPayload.ScriptSlot? chosen = null;

            foreach (var name in new[] { "externalLinks.js", "app.js", "navFade.js", "navigateBack.js" })
            {
                var candidate = located.FindScript(name);
                if (candidate is null || candidate.DataLength < script.Length)
                {
                    continue;
                }

                chosen = candidate;
                break;
            }

            if (chosen is null)
            {
                return "mute button skipped (no slot with room)";
            }

            var baseBytes = GetPristineScriptBase(located, backupPath, chosen);
            if (baseBytes is null)
            {
                return "mute button skipped (no clean script base available)";
            }

            var content = Encoding.UTF8.GetBytes(CompactScriptBase(baseBytes) + script);
            if (content.Length > chosen.DataLength)
            {
                return $"mute button skipped (does not fit {chosen.Name})";
            }

            return LauncherPayload.SwapScriptInPlace(path, located, chosen, LauncherPayload.PadTextToExactSize(content, chosen.DataLength)) + $" (slot: {chosen.Name})";
        }
        catch (Exception exception)
        {
            return "mute button skipped: " + exception.Message;
        }
    }

    /// <summary>
    /// The mute tile, placed in the launcher's left rail above the Settings icon (`mud-nav-link` styling so
    /// it matches the other tiles). A speaker icon with a slash that appears while muted; state in
    /// localStorage, and the rotator is told through a "cm" event.
    /// </summary>
    internal static string BuildMuteButtonScript()
    {
        return "//CLBM\n"
            + "(function(){function m(){return localStorage.getItem('cm')==='1'}\n"
            + "var b=document.createElement('div');b.className='mud-nav-link';\n"
            + "b.innerHTML='<svg width=\"26\" height=\"26\" viewBox=\"0 0 24 24\" fill=\"currentColor\"><path d=\"M4 9v6h3l5 4V5l-5 4H4z\"/><path d=\"M17 9l5 6\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\"/></svg>';\n"
            + "var x=b.firstChild.lastChild;\n"
            + "function p(){x.style.display=m()?'':'none'}\n"
            + "b.onclick=function(){localStorage.setItem('cm',m()?'0':'1');p();document.dispatchEvent(new Event('cm'))};\n"
            + "function q(){var r=document.querySelector('.nav-bottom');if(r&&!b.parentNode){r.insertBefore(b,r.firstChild);p()}}\n"
            + "setInterval(q,2000);q()})();\n";
    }

    /// <summary>Returns the slot size of the first local launcher file found, or null.</summary>
    internal static int? FindLocalSlotSize(params string[] directories)
    {
        foreach (var directory in directories)
        {
            foreach (var fileName in LauncherFileNames)
            {
                var path = Path.Combine(directory, fileName);
                if (!File.Exists(path))
                {
                    continue;
                }

                try
                {
                    return LauncherPayload.LoadAndLocate(path).Region.DataLength;
                }
                catch
                {
                    // try the next one
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Produces a ready-to-apply payload (fitted + padded to the slot size) for a target slot,
    /// or null when the image cannot be encoded to fit.
    /// </summary>
    internal static byte[]? BuildPayload(string imagePath, int slotSize)
    {
        try
        {
            var fitted = FitToSlot(File.ReadAllBytes(imagePath), slotSize);
            if (fitted is null)
            {
                return null;
            }

            return LauncherPayload.PadToExactSize(fitted, slotSize, LauncherPayload.Marker + Path.GetFileName(imagePath));
        }
        catch (Exception ex) when (ex is LauncherPayload.LauncherPatchException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Returns JPEG bytes that fit the slot: the source untouched when it already is a fitting JPEG,
    /// otherwise re-encoded/downscaled via System.Drawing (Windows only).
    /// </summary>
    internal static byte[]? FitToSlot(byte[] source, int slotSize)
    {
        var sourceIsJpeg = source.Length >= 3 && source[0] == 0xFF && source[1] == 0xD8 && source[2] == 0xFF;
        if (sourceIsJpeg && source.Length <= slotSize)
        {
            return source;
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new LauncherPayload.LauncherPatchException("image does not fit and re-encoding requires Windows - skipped");
        }

        return FitJpeg(source, slotSize);
    }

    /// <summary>Encodes the image (downscaling when needed) until it fits into the byte budget.</summary>
    [SupportedOSPlatform("windows")]
    internal static byte[]? FitJpeg(byte[] imageBytes, int budget)
    {
        using var source = LoadWithOrientation(imageBytes);
        if (source is null)
        {
            return null;
        }

        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

        var scale = 1.0;
        for (var round = 0; round < 7; round++)
        {
            var width = Math.Max(1, (int)Math.Round(source.Width * scale));
            var height = Math.Max(1, (int)Math.Round(source.Height * scale));

            using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            bitmap.SetResolution(96, 96);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.SmoothingMode = SmoothingMode.HighQuality;
                graphics.DrawImage(source, 0, 0, width, height);
            }

            foreach (var quality in new[] { 90, 84, 78, 72, 66, 60, 54, 48 })
            {
                using var ms = new MemoryStream();
                using var encoderParams = new EncoderParameters(1);
                encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);
                bitmap.Save(ms, codec, encoderParams);

                if (ms.Length <= budget)
                {
                    return ms.ToArray();
                }
            }

            scale *= 0.8;
        }

        return null;
    }

    [SupportedOSPlatform("windows")]
    private static Bitmap? LoadWithOrientation(byte[] imageBytes)
    {
        using var stream = new MemoryStream(imageBytes);
        using var src = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false);

        var orientation = 1;
        try
        {
            if (src.PropertyIdList.Contains(0x0112))
            {
                var item = src.GetPropertyItem(0x0112);
                if (item?.Value is { Length: > 0 } raw)
                {
                    orientation = raw[0];
                }
            }
        }
        catch
        {
            // ignore missing/broken metadata
        }

        var rotateFlip = orientation switch
        {
            3 => RotateFlipType.Rotate180FlipNone,
            6 => RotateFlipType.Rotate90FlipNone,
            8 => RotateFlipType.Rotate270FlipNone,
            _ => RotateFlipType.RotateNoneFlipNone,
        };

        var bitmap = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb);
        bitmap.SetResolution(src.HorizontalResolution > 0 ? src.HorizontalResolution : 96, src.VerticalResolution > 0 ? src.VerticalResolution : 96);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.DrawImage(src, 0, 0, src.Width, src.Height);
        }

        if (rotateFlip != RotateFlipType.RotateNoneFlipNone)
        {
            bitmap.RotateFlip(rotateFlip);
        }

        return bitmap;
    }
}
