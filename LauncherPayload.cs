using System.Text;

namespace PreyToLive.CustomLauncherBackgrounds;

/// <summary>
/// Shared launcher-background payload helpers. Used by the server mod (local patching) and by the
/// standalone patcher tool for remote players, so both apply byte-identical payloads.
/// </summary>
internal static class LauncherPayload
{
    /// <summary>Marker embedded in the first JPEG COM segment of a patched background.</summary>
    public const string Marker = "CLB-BG-PATCH:";

    public sealed class LauncherPatchException(string message) : Exception(message);

    /// <summary>A front-end script slot (navFade.js / app.js) inside the launcher file.</summary>
    public sealed record ScriptSlot(string Name, int DataOffset, int DataLength);

    public sealed record LocatedLauncher(byte[] FileBytes, LauncherBundle.Entry Entry, LauncherBundle.BgRegion Region, IReadOnlyList<ScriptSlot> Scripts)
    {
        public int DataOffset => (int)(Entry.Offset + Region.DataOffset);

        public ScriptSlot? FindScript(string name) => Scripts.FirstOrDefault(slot => slot.Name == name);
    }

    /// <summary>
    /// Reads a launcher file and locates the embedded background region.
    /// Throws <see cref="LauncherPatchException"/> with a friendly message when the launcher build is not supported.
    /// </summary>
    public static LocatedLauncher LoadAndLocate(string launcherPath)
    {
        var file = File.ReadAllBytes(launcherPath);

        var entry = LauncherBundle.FindLauncherEntry(file)
            ?? throw new LauncherPatchException("unsupported launcher build (bundle entry not found) - skipped");

        var dll = new byte[entry.Size];
        Array.Copy(file, entry.Offset, dll, 0, entry.Size);

        var region = LauncherBundle.FindBgRegion(dll)
            ?? throw new LauncherPatchException("unsupported launcher build (background resource not found) - skipped");

        var dataOffset = entry.Offset + region.DataOffset;
        if (dataOffset < 0 || dataOffset > int.MaxValue || dataOffset + region.DataLength > file.Length)
        {
            throw new LauncherPatchException("unsupported launcher build (background out of range) - skipped");
        }

        if (file[dataOffset] != 0xFF || file[dataOffset + 1] != 0xD8 || file[dataOffset + 2] != 0xFF)
        {
            throw new LauncherPatchException("launcher background region does not contain a JPEG - skipped");
        }

        var scripts = LauncherBundle.FindScriptRegions(dll)
            .Select(script => new ScriptSlot(script.Name, (int)(entry.Offset + script.Region.DataOffset), script.Region.DataLength))
            .ToList();

        return new LocatedLauncher(file, entry, region, scripts);
    }

    /// <summary>
    /// Replaces the background region with a payload of the exact same length (in place, nothing moves),
    /// keeps/refreshes the one-time .original backup and verifies the write afterwards.
    /// </summary>
    public static string SwapInPlace(string launcherPath, LocatedLauncher located, byte[] payload)
    {
        return SwapRegion(
            launcherPath,
            located,
            located.DataOffset,
            located.Region.DataLength,
            payload,
            requireJpeg: true,
            handleBackup: true,
            description: "applied");
    }

    /// <summary>
    /// Replaces one of the launcher's embedded front-end scripts (navFade.js / app.js) with the
    /// background-rotation script. Content is padded to the exact slot length; no backup handling
    /// (the background swap in the same patch run owns the backup).
    /// </summary>
    public static string SwapScriptInPlace(string launcherPath, LocatedLauncher located, ScriptSlot slot, byte[] content)
    {
        var padded = PadTextToExactSize(content, slot.DataLength);

        return SwapRegion(
            launcherPath,
            located,
            slot.DataOffset,
            slot.DataLength,
            padded,
            requireJpeg: false,
            handleBackup: false,
            description: $"applied launcher script ({slot.Name})");
    }

    /// <summary>True when the given region of the data contains the text.</summary>
    public static bool RegionContainsText(byte[] data, int offset, int length, string text)
    {
        var needle = Encoding.ASCII.GetBytes(text);
        var end = Math.Min(data.Length, offset + length);
        for (var i = offset; i <= end - needle.Length; i++)
        {
            var match = true;
            for (var k = 0; k < needle.Length; k++)
            {
                if (data[i + k] != needle[k])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Pads text content to an exact size with trailing spaces (valid at the end of a script).</summary>
    public static byte[] PadTextToExactSize(byte[] content, int target)
    {
        if (content.Length > target)
        {
            throw new LauncherPatchException($"content is {content.Length} bytes but the launcher slot only fits {target} bytes");
        }

        if (content.Length == target)
        {
            return content;
        }

        var padded = new byte[target];
        Array.Copy(content, padded, content.Length);
        for (var i = content.Length; i < target; i++)
        {
            padded[i] = (byte)' ';
        }

        return padded;
    }

    private static string SwapRegion(string launcherPath, LocatedLauncher located, int dataOffset, int regionLength, byte[] payload, bool requireJpeg, bool handleBackup, string description)
    {
        if (payload.Length != regionLength)
        {
            throw new LauncherPatchException($"payload is {payload.Length} bytes but the launcher slot is {regionLength} bytes");
        }

        if (requireJpeg && (payload[0] != 0xFF || payload[1] != 0xD8))
        {
            throw new LauncherPatchException("payload is not a JPEG");
        }

        // Backup: created on first use, refreshed when the current launcher is pristine (e.g. after an SPT update).
        var backupPath = launcherPath + ".original";
        if (handleBackup)
        {
            var currentIsOurPatch = ContainsMarker(located.FileBytes, located.DataOffset);
            if (!File.Exists(backupPath) || !currentIsOurPatch)
            {
                File.Copy(launcherPath, backupPath, overwrite: true);
            }
        }

        Array.Copy(payload, 0, located.FileBytes, dataOffset, payload.Length);

        var tmpPath = launcherPath + ".clb-tmp";
        try
        {
            File.WriteAllBytes(tmpPath, located.FileBytes);
            File.Move(tmpPath, launcherPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmpPath))
            {
                try
                {
                    File.Delete(tmpPath);
                }
                catch
                {
                    // best effort
                }
            }
        }

        // Verify that what we intended to write actually landed in the file.
        var verify = File.ReadAllBytes(launcherPath);
        for (var i = 0; i < payload.Length; i++)
        {
            if (verify[dataOffset + i] != payload[i])
            {
                throw new IOException("verification after writing failed");
            }
        }

        return handleBackup
            ? $"{description} ({payload.Length} bytes), backup: {Path.GetFileName(backupPath)}"
            : $"{description} ({payload.Length} bytes)";
    }

    /// <summary>True when the data contains one of our markers anywhere (whole-file check).</summary>
    public static bool ContainsMarkerAnywhere(byte[] data)
    {
        return data.AsSpan().IndexOf(Encoding.ASCII.GetBytes(Marker)) >= 0;
    }

    /// <summary>True when the background region already carries one of our markers.</summary>
    public static bool ContainsMarker(byte[] fileBytes, int dataOffset)
    {
        var marker = Encoding.ASCII.GetBytes(Marker);
        var end = Math.Min(fileBytes.Length, dataOffset + 160);
        for (var i = dataOffset; i <= end - marker.Length; i++)
        {
            var match = true;
            for (var k = 0; k < marker.Length; k++)
            {
                if (fileBytes[i + k] != marker[k])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Extracts the file name recorded in the patch marker, if present.</summary>
    public static string? GetMarkerName(byte[] jpeg)
    {
        var marker = Encoding.ASCII.GetBytes(Marker);
        var limit = Math.Min(jpeg.Length - marker.Length, 512);
        for (var i = 0; i < limit; i++)
        {
            var match = true;
            for (var k = 0; k < marker.Length; k++)
            {
                if (jpeg[i + k] != marker[k])
                {
                    match = false;
                    break;
                }
            }

            if (!match)
            {
                continue;
            }

            var start = i + marker.Length;
            var sb = new StringBuilder();
            for (var j = start; j < jpeg.Length && sb.Length < 120; j++)
            {
                var b = jpeg[j];
                if (b < 32 || b > 126)
                {
                    break;
                }

                sb.Append((char)b);
            }

            return sb.Length > 0 ? sb.ToString() : null;
        }

        return null;
    }

    /// <summary>
    /// Pads a JPEG to an exact size using valid COM segments inserted after the SOI marker
    /// (plus up to 3 trailing zero bytes after EOI if the remaining gap is smaller than a segment).
    /// The marker text is stored in the first segment so a patched launcher identifies its background.
    /// </summary>
    public static byte[] PadToExactSize(byte[] jpeg, int target, string markerText)
    {
        if (jpeg.Length > target)
        {
            throw new LauncherPatchException($"image is {jpeg.Length} bytes but the launcher slot only fits {target} bytes");
        }

        if (jpeg.Length == target)
        {
            return jpeg;
        }

        var fill = target - jpeg.Length;
        var output = new List<byte>(target) { 0xFF, 0xD8 };

        var markerBytes = Encoding.ASCII.GetBytes(markerText);
        if (markerBytes.Length > 64)
        {
            markerBytes = markerBytes[..64];
        }

        var markerWritten = false;
        var remaining = fill;

        if (remaining >= 4 + markerBytes.Length)
        {
            AppendComSegment(output, markerBytes);
            remaining -= 4 + markerBytes.Length;
            markerWritten = true;
        }

        while (remaining >= 4)
        {
            var payload = Math.Min(65533, remaining - 4);
            var payloadBytes = new byte[payload];
            if (!markerWritten && markerBytes.Length <= payload)
            {
                Array.Copy(markerBytes, payloadBytes, markerBytes.Length);
                markerWritten = true;
            }

            AppendComSegment(output, payloadBytes);
            remaining -= payload + 4;
        }

        output.AddRange(jpeg.AsSpan(2).ToArray());

        // leftover gap smaller than a COM segment: pad after EOI (decoders stop at EOI)
        for (var i = 0; i < remaining; i++)
        {
            output.Add(0);
        }

        if (output.Count != target)
        {
            throw new InvalidOperationException($"padding produced {output.Count} bytes, expected {target}");
        }

        return output.ToArray();
    }

    private static void AppendComSegment(List<byte> output, byte[] payload)
    {
        var length = payload.Length + 2;
        output.Add(0xFF);
        output.Add(0xFE);
        output.Add((byte)(length >> 8));
        output.Add((byte)(length & 0xFF));
        output.AddRange(payload);
    }
}
