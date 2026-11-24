using System.Buffers.Binary;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

namespace PreyToLive.CustomLauncherBackgrounds;

/// <summary>
/// Locates the launcher background image inside SPT.Launcher.exe / SPT.Launcher.Linux.
///
/// The launchers are .NET single-file bundles (stored uncompressed) containing the launcher assembly
/// (SPT.Launcher.dll / SPT.Launcher.Linux.dll), whose embedded wwwroot resources contain "images/bg.jpg"
/// as a standard CLI resource: [int32 length][JPEG bytes].
///
/// We never move or resize anything - only the JPEG bytes inside that fixed-size region are replaced,
/// so all bundle/resource tables (offsets, lengths) stay valid.
/// </summary>
internal static class LauncherBundle
{
    private static readonly string[] LauncherAssemblyNames = ["SPT.Launcher.dll", "SPT.Launcher.Linux.dll"];

    internal sealed record Entry(long Offset, long Size);

    internal sealed record BgRegion(int DataOffset, int DataLength);

    /// <summary>Finds the launcher assembly inside the single-file bundle (entry record scan + validation).</summary>
    internal static Entry? FindLauncherEntry(byte[] file)
    {
        foreach (var name in LauncherAssemblyNames)
        {
            var entry = FindEntry(file, name);
            if (entry is not null)
            {
                return entry;
            }
        }

        return null;
    }

    private static Entry? FindEntry(byte[] file, string entryName)
    {
        var nameBytes = Encoding.UTF8.GetBytes(entryName);

        for (var i = 0; i + 26 + nameBytes.Length <= file.Length; i++)
        {
            // entry record: i=offset, i+8=size, i+16=compressedSize, i+24=type, i+25=name length, i+26=name
            if (file[i + 24] != 1 || file[i + 25] != nameBytes.Length)
            {
                continue;
            }

            var match = true;
            for (var k = 0; k < nameBytes.Length; k++)
            {
                if (file[i + 26 + k] != nameBytes[k])
                {
                    match = false;
                    break;
                }
            }

            if (!match)
            {
                continue;
            }

            var offset = BinaryPrimitives.ReadInt64LittleEndian(file.AsSpan(i));
            var size = BinaryPrimitives.ReadInt64LittleEndian(file.AsSpan(i + 8));
            var compressed = BinaryPrimitives.ReadInt64LittleEndian(file.AsSpan(i + 16));

            if (offset <= 0 || size <= 0 || offset + size > file.Length || compressed != 0)
            {
                continue;
            }

            if (size < 2 || file[offset] != (byte)'M' || file[offset + 1] != (byte)'Z')
            {
                continue;
            }

            return new Entry(offset, size);
        }

        return null;
    }

    /// <summary>Finds the embedded launcher background (wwwroot/images/bg.jpg) inside the launcher assembly.</summary>
    internal static BgRegion? FindBgRegion(byte[] dll)
    {
        return FindResourceRegion(dll, ".wwwroot.images.bg.jpg", 20_000, 4 * 1024 * 1024, requireJpeg: true);
    }

    internal sealed record ScriptResource(string Name, BgRegion Region);

    /// <summary>
    /// Finds the launcher's embedded front-end text resources we patch:
    /// the scripts (navFade.js preferred for the rotator; app.js as the fallback slot) and the stylesheet
    /// that carries the background overlay (used to drop its backdrop blur).
    /// </summary>
    internal static IReadOnlyList<ScriptResource> FindScriptRegions(byte[] dll)
    {
        var scripts = new List<ScriptResource>();

        var navFade = FindResourceRegion(dll, ".wwwroot.js.scripts.navFade.js", 100, 200_000, requireJpeg: false);
        if (navFade is not null)
        {
            scripts.Add(new ScriptResource("navFade.js", navFade));
        }

        var appJs = FindResourceRegion(dll, ".wwwroot.js.app.js", 50, 200_000, requireJpeg: false);
        if (appJs is not null)
        {
            scripts.Add(new ScriptResource("app.js", appJs));
        }

        // The roomiest slot on current builds - its original content minifies down enough that the whole
        // rotator (audio, video crossfade, card hiding) fits comfortably here.
        var userTabset = FindResourceRegion(dll, ".wwwroot.js.scripts.userTabset.js", 100, 200_000, requireJpeg: false);
        if (userTabset is not null)
        {
            scripts.Add(new ScriptResource("userTabset.js", userTabset));
        }

        // Second roomy slot - carries the mute button (the rotator fills the userTabset slot).
        var externalLinks = FindResourceRegion(dll, ".wwwroot.js.scripts.externalLinks.js", 100, 200_000, requireJpeg: false);
        if (externalLinks is not null)
        {
            scripts.Add(new ScriptResource("externalLinks.js", externalLinks));
        }

        var backgroundsCss = FindResourceRegion(dll, ".wwwroot.css.styles.backgrounds.css", 100, 500_000, requireJpeg: false);
        if (backgroundsCss is not null)
        {
            scripts.Add(new ScriptResource("backgrounds.css", backgroundsCss));
        }

        return scripts;
    }

    /// <summary>Finds an embedded wwwroot resource by name suffix.</summary>
    internal static BgRegion? FindResourceRegion(byte[] dll, string nameSuffix, int minLength, int maxLength, bool requireJpeg)
    {
        using var ms = new MemoryStream(dll, writable: false);
        using var pe = new PEReader(ms);
        var md = pe.GetMetadataReader();

        var resDirRva = pe.PEHeaders.CorHeader?.ResourcesDirectory.RelativeVirtualAddress ?? 0;
        if (resDirRva == 0)
        {
            return null;
        }

        foreach (var handle in md.ManifestResources)
        {
            var res = md.GetManifestResource(handle);
            var name = md.GetString(res.Name);
            if (!name.EndsWith(nameSuffix, StringComparison.Ordinal))
            {
                continue;
            }

            var fileOffset = MapRvaToFileOffset(pe, resDirRva + (int)res.Offset);
            if (fileOffset < 0 || fileOffset + 4 > dll.Length)
            {
                continue;
            }

            var length = BinaryPrimitives.ReadInt32LittleEndian(dll.AsSpan(fileOffset));
            if (length >= minLength && length <= maxLength && (!requireJpeg || LooksLikeJpeg(dll, fileOffset + 4)))
            {
                return new BgRegion(fileOffset + 4, length);
            }

            // defensive fallback: the resource points straight at the data (no length prefix)
            if (requireJpeg && LooksLikeJpeg(dll, fileOffset))
            {
                var jpegEnd = FindJpegEnd(dll, fileOffset);
                if (jpegEnd > fileOffset && jpegEnd - fileOffset >= minLength && jpegEnd - fileOffset <= maxLength)
                {
                    return new BgRegion(fileOffset, jpegEnd - fileOffset);
                }
            }
        }

        return null;
    }

    private static bool LooksLikeJpeg(byte[] data, int offset)
    {
        return offset >= 0
            && offset + 3 <= data.Length
            && data[offset] == 0xFF
            && data[offset + 1] == 0xD8
            && data[offset + 2] == 0xFF;
    }

    private static int FindJpegEnd(byte[] data, int start)
    {
        for (var i = start + 2; i + 1 < data.Length; i++)
        {
            if (data[i] == 0xFF && data[i + 1] == 0xD9)
            {
                return i + 2;
            }
        }

        return -1;
    }

    private static int MapRvaToFileOffset(PEReader pe, int rva)
    {
        foreach (var section in pe.PEHeaders.SectionHeaders)
        {
            if (rva >= section.VirtualAddress && rva < section.VirtualAddress + section.VirtualSize)
            {
                return rva - section.VirtualAddress + section.PointerToRawData;
            }
        }

        return -1;
    }
}
