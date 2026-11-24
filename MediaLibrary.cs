namespace PreyToLive.CustomLauncherBackgrounds;

/// <summary>Kind of a background file.</summary>
internal enum MediaKind
{
    Image,
    Video,
}

/// <summary>One background file inside the mod's media folders.</summary>
internal sealed record MediaFileInfo(string FileName, MediaKind Kind, long SizeBytes, int? Width, int? Height);

/// <summary>Options for the image formatter (used by the web editor's upload form).</summary>
internal sealed record MediaFormatOptions(int TargetWidth, int TargetHeight, bool CropToFill, string OutputFormat, string SourceExtension, bool BlurFillBackground = false, int DeblockAmountPercent = 0, int DenoiseAmountPercent = 0);

/// <summary>
/// Manages the background media folders (res/Images, res/Videos): listing, numbered imports and
/// deletions. Pure file system logic so it can be unit-tested outside the server.
/// </summary>
internal static class MediaLibrary
{
    public static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif"];
    public static readonly string[] VideoExtensions = [".mp4"];

    public static string ImagesDirectory(string modDirectory) => Path.Combine(modDirectory, "res", "Images");

    public static string VideosDirectory(string modDirectory) => Path.Combine(modDirectory, "res", "Videos");

    public static bool IsImage(string fileName) => ImageExtensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase);

    public static bool IsVideo(string fileName) => VideoExtensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Next free number for the "(number)-bg.ext" naming, counted per kind: images number within
    /// res/Images and videos within res/Videos, each starting at 1. Returns 1 when that folder is empty.
    /// </summary>
    public static int NextNumber(string modDirectory, MediaKind kind)
    {
        var directory = kind == MediaKind.Image ? ImagesDirectory(modDirectory) : VideosDirectory(modDirectory);
        var maximum = 0;

        if (Directory.Exists(directory))
        {
            foreach (var file in Directory.GetFiles(directory))
            {
                var name = Path.GetFileName(file);
                var dash = name.IndexOf('-');
                if (dash > 0 && int.TryParse(name[..dash], out var number) && number > maximum)
                {
                    maximum = number;
                }
            }
        }

        return maximum + 1;
    }

    /// <summary>Lists all media files (natural order) with size and, for images, pixel dimensions.</summary>
    public static IReadOnlyList<MediaFileInfo> List(string modDirectory)
    {
        var result = new List<MediaFileInfo>();

        foreach (var (directory, kind) in new[] { (ImagesDirectory(modDirectory), MediaKind.Image), (VideosDirectory(modDirectory), MediaKind.Video) })
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            var files = Directory.GetFiles(directory).OrderBy(Path.GetFileName, Comparer<string?>.Create(NaturalOrder.Compare));
            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                if (!IsImage(name) && !IsVideo(name))
                {
                    continue;
                }

                var size = new FileInfo(file).Length;
                var dimensions = kind == MediaKind.Image ? MediaFormatter.TryReadImageSize(file) : null;
                result.Add(new MediaFileInfo(name, kind, size, dimensions?.Width, dimensions?.Height));
            }
        }

        return result;
    }

    /// <summary>
    /// Returns the name of an existing file of the same kind whose content is identical (same length, then
    /// the same SHA-256), or null when the content is new. Cheap: only files with a matching size are hashed.
    /// </summary>
    public static string? FindDuplicate(string modDirectory, byte[] content, MediaKind kind)
    {
        var directory = kind == MediaKind.Image ? ImagesDirectory(modDirectory) : VideosDirectory(modDirectory);
        if (!Directory.Exists(directory))
        {
            return null;
        }

        byte[]? contentHash = null;

        foreach (var file in Directory.GetFiles(directory))
        {
            if (new FileInfo(file).Length != content.LongLength)
            {
                continue;
            }

            contentHash ??= System.Security.Cryptography.SHA256.HashData(content);
            using var stream = File.OpenRead(file);
            var existing = System.Security.Cryptography.SHA256.HashData(stream);

            if (existing.AsSpan().SequenceEqual(contentHash))
            {
                return Path.GetFileName(file);
            }
        }

        return null;
    }

    private sealed record UploadOrigin(string File, string Original, string Hash);

    private static string ManifestPath(string modDirectory) => Path.Combine(modDirectory, "cache", "upload-origins.json");

    private static List<UploadOrigin> LoadManifest(string modDirectory)
    {
        try
        {
            var path = ManifestPath(modDirectory);
            if (!File.Exists(path))
            {
                return [];
            }

            return System.Text.Json.JsonSerializer.Deserialize<List<UploadOrigin>>(File.ReadAllText(path)) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static void SaveManifest(string modDirectory, List<UploadOrigin> entries)
    {
        try
        {
            var path = ManifestPath(modDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(entries, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // best effort - the media files matter more than the bookkeeping
        }
    }

    private static string Hash(byte[] content) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant();

    /// <summary>
    /// Stored file that was previously imported from the given original upload name (and still exists), or
    /// null. Used to replace an image/video in place when a new version of the same source file is uploaded.
    /// </summary>
    public static string? FindStoredForOriginal(string modDirectory, string originalName)
    {
        if (string.IsNullOrWhiteSpace(originalName))
        {
            return null;
        }

        var entry = LoadManifest(modDirectory).FirstOrDefault(candidate => string.Equals(candidate.Original, originalName, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return null;
        }

        foreach (var directory in new[] { ImagesDirectory(modDirectory), VideosDirectory(modDirectory) })
        {
            if (File.Exists(Path.Combine(directory, entry.File)))
            {
                return entry.File;
            }
        }

        return null;
    }

    /// <summary>
    /// Overwrites an existing stored file with new content, keeping its name/number, and refreshes the
    /// recorded hash so re-uploading the same new version is recognised as a duplicate afterwards.
    /// </summary>
    public static string? Replace(string modDirectory, string storedFileName, byte[] content)
    {
        foreach (var directory in new[] { ImagesDirectory(modDirectory), VideosDirectory(modDirectory) })
        {
            var path = Path.Combine(directory, storedFileName);
            if (!File.Exists(path))
            {
                continue;
            }

            File.WriteAllBytes(path, content);

            var entries = LoadManifest(modDirectory);
            var index = entries.FindIndex(entry => string.Equals(entry.File, storedFileName, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                entries[index] = entries[index] with { Hash = Hash(content) };
                SaveManifest(modDirectory, entries);
            }

            return storedFileName;
        }

        return null;
    }

    /// <summary>SHA-256 of a file on disk (streamed - works for gigabyte videos).</summary>
    public static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>File based twin of <see cref="FindDuplicate"/>: the content comes from a path, not memory.</summary>
    public static string? FindDuplicateFromFile(string modDirectory, string contentPath, MediaKind kind)
    {
        var directory = kind == MediaKind.Image ? ImagesDirectory(modDirectory) : VideosDirectory(modDirectory);
        if (!Directory.Exists(directory))
        {
            return null;
        }

        var length = new FileInfo(contentPath).Length;
        string? contentHash = null;

        foreach (var file in Directory.GetFiles(directory))
        {
            if (new FileInfo(file).Length != length)
            {
                continue;
            }

            contentHash ??= HashFile(contentPath);
            if (HashFile(file) == contentHash)
            {
                return Path.GetFileName(file);
            }
        }

        return null;
    }

    /// <summary>File based import: copies a finished file into the kind folder as the next numbered name.</summary>
    public static string ImportFromFile(string modDirectory, string contentPath, MediaKind kind, string extension, string? originalName = null)
    {
        var directory = kind == MediaKind.Image ? ImagesDirectory(modDirectory) : VideosDirectory(modDirectory);
        Directory.CreateDirectory(directory);

        var fileName = $"{NextNumber(modDirectory, kind)}-bg{extension}";
        var target = Path.Combine(directory, fileName);
        File.Copy(contentPath, target, overwrite: true);

        if (!string.IsNullOrWhiteSpace(originalName))
        {
            var entries = LoadManifest(modDirectory);
            entries.RemoveAll(entry => string.Equals(entry.Original, originalName, StringComparison.OrdinalIgnoreCase));
            entries.Add(new UploadOrigin(fileName, originalName, HashFile(target)));
            SaveManifest(modDirectory, entries);
        }

        return fileName;
    }

    /// <summary>File based replace: overwrites a stored file with new content from disk and refreshes the hash.</summary>
    public static string? ReplaceFromFile(string modDirectory, string storedFileName, string contentPath)
    {
        foreach (var directory in new[] { ImagesDirectory(modDirectory), VideosDirectory(modDirectory) })
        {
            var path = Path.Combine(directory, storedFileName);
            if (!File.Exists(path))
            {
                continue;
            }

            File.Copy(contentPath, path, overwrite: true);

            var entries = LoadManifest(modDirectory);
            var index = entries.FindIndex(entry => string.Equals(entry.File, storedFileName, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                entries[index] = entries[index] with { Hash = HashFile(path) };
                SaveManifest(modDirectory, entries);
            }

            return storedFileName;
        }

        return null;
    }

    /// <summary>
    /// Writes formatted media as the next numbered file ("(n)-bg.png" / ".jpg" / ".mp4"). When the original
    /// upload name is given it is remembered so a later upload of the same source replaces this file.
    /// </summary>
    public static string Import(string modDirectory, byte[] content, MediaKind kind, string extension, string? originalName = null)
    {
        var directory = kind == MediaKind.Image ? ImagesDirectory(modDirectory) : VideosDirectory(modDirectory);
        Directory.CreateDirectory(directory);

        var fileName = $"{NextNumber(modDirectory, kind)}-bg{extension}";
        File.WriteAllBytes(Path.Combine(directory, fileName), content);

        if (!string.IsNullOrWhiteSpace(originalName))
        {
            var entries = LoadManifest(modDirectory);
            entries.RemoveAll(entry => string.Equals(entry.Original, originalName, StringComparison.OrdinalIgnoreCase));
            entries.Add(new UploadOrigin(fileName, originalName, Hash(content)));
            SaveManifest(modDirectory, entries);
        }

        return fileName;
    }

    /// <summary>
    /// Closes numbering gaps in res/Images and res/Videos: the "(n)-bg.ext" files of each kind are renamed
    /// to a contiguous 1..N sequence (3-bg.png -> 2-bg.png ...), ascending so renames never collide. Files
    /// that do not follow the mod's naming are left alone, and a rename is skipped when its target already
    /// exists. Returns how many files were renamed (0 = already contiguous).
    /// </summary>
    public static int Renumber(string modDirectory)
    {
        var renamed = 0;
        var renames = new List<(string Old, string New)>();

        foreach (var kind in new[] { MediaKind.Image, MediaKind.Video })
        {
            var directory = kind == MediaKind.Image ? ImagesDirectory(modDirectory) : VideosDirectory(modDirectory);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            var number = 0;
            var files = Directory.GetFiles(directory)
                .OrderBy(Path.GetFileName, Comparer<string?>.Create(NaturalOrder.Compare))
                .ToArray();

            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                var match = System.Text.RegularExpressions.Regex.Match(name, @"^(\d+)-bg(\.[^.]+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (!match.Success)
                {
                    continue;
                }

                number++;
                var expected = $"{number}-bg{match.Groups[2].Value.ToLowerInvariant()}";
                if (string.Equals(name, expected, StringComparison.Ordinal))
                {
                    continue;
                }

                var target = Path.Combine(directory, expected);
                if (File.Exists(target))
                {
                    continue;
                }

                File.Move(file, target);
                renames.Add((name, expected));
                renamed++;
            }
        }

        if (renames.Count > 0)
        {
            var entries = LoadManifest(modDirectory);
            var touched = false;

            for (var index = 0; index < entries.Count; index++)
            {
                var match = renames.FirstOrDefault(candidate => string.Equals(candidate.Old, entries[index].File, StringComparison.OrdinalIgnoreCase));
                if (match.Old is not null)
                {
                    entries[index] = entries[index] with { File = match.New };
                    touched = true;
                }
            }

            if (touched)
            {
                SaveManifest(modDirectory, entries);
            }
        }

        return renamed;
    }

    /// <summary>Deletes one media file. Only plain file names inside res/Images or res/Videos are accepted.</summary>
    public static string? Delete(string modDirectory, string fileName, MediaKind kind)
    {
        if (string.IsNullOrWhiteSpace(fileName)
            || fileName.Contains('/')
            || fileName.Contains('\\')
            || fileName.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        var directory = kind == MediaKind.Image ? ImagesDirectory(modDirectory) : VideosDirectory(modDirectory);
        var fullPath = Path.GetFullPath(Path.Combine(directory, fileName));
        if (!fullPath.StartsWith(Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!File.Exists(fullPath))
        {
            return null;
        }

        File.Delete(fullPath);

        var entries = LoadManifest(modDirectory);
        if (entries.RemoveAll(entry => string.Equals(entry.File, fileName, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            SaveManifest(modDirectory, entries);
        }

        return fileName;
    }

    /// <summary>
    /// Deletes several files of one kind (each validated the same way as <see cref="Delete"/>), then closes
    /// the numbering gaps once for the whole folder. Returns the names actually deleted.
    /// </summary>
    public static IReadOnlyList<string> DeleteMany(string modDirectory, IEnumerable<string> fileNames, MediaKind kind)
    {
        var directory = kind == MediaKind.Image ? ImagesDirectory(modDirectory) : VideosDirectory(modDirectory);

        var deleted = new List<string>();
        foreach (var fileName in fileNames)
        {
            if (string.IsNullOrWhiteSpace(fileName)
                || fileName.Contains('/')
                || fileName.Contains('\\')
                || fileName.Contains("..", StringComparison.Ordinal))
            {
                continue;
            }

            var fullPath = Path.GetFullPath(Path.Combine(directory, fileName));
            if (!fullPath.StartsWith(Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
            {
                continue;
            }

            File.Delete(fullPath);
            deleted.Add(fileName);
        }

        if (deleted.Count > 0)
        {
            var entries = LoadManifest(modDirectory);
            if (entries.RemoveAll(entry => deleted.Any(name => string.Equals(entry.File, name, StringComparison.OrdinalIgnoreCase))) > 0)
            {
                SaveManifest(modDirectory, entries);
            }

            Renumber(modDirectory);
        }

        return deleted;
    }
}
