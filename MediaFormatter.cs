namespace PreyToLive.CustomLauncherBackgrounds;

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Drawing.Imaging;
using Encoder = System.Drawing.Imaging.Encoder;

/// <summary>
/// Formats uploaded media for use as launcher backgrounds:
/// images are converted (PNG/JPEG), optionally resized/cropped to the launcher's aspect, and mp4
/// files get a container sanity check. Image work uses System.Drawing and therefore needs a Windows
/// host - callers fall back to storing the original file when formatting is unavailable.
/// </summary>
internal static class MediaFormatter
{
    /// <summary>Reads the pixel size of an image file. Works on any platform by reading file headers directly.</summary>
    public static (int Width, int Height)? TryReadImageSize(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var header = new byte[MaxHeaderBytes];
            var read = stream.Read(header, 0, MaxHeaderBytes);
            if (read < 4) return null;

            // PNG: bytes 16-19 = width, 20-23 = height (big-endian)
            if (header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47) // \x89PNG
            {
                if (read < 24) return null;
                return (BigEndianInt32(header, 16), BigEndianInt32(header, 20));
            }

            // JPEG: SOI (FF D8) + APP0 (FF E0) + JFIF header
            if (header[0] == 0xFF && header[1] == 0xD8)
            {
                return ReadJpegSize(stream);
            }

            // BMP: 'BM' signature
            if (header[0] == 0x42 && header[1] == 0x4D)
            {
                if (read < 26) return null;
                return (BitConverter.ToInt32(header, 18), BitConverter.ToInt32(header, 22));
            }

            // WebP: 'WEBP' signature
            if (header[4] == 0x57 && header[5] == 0x45 && header[6] == 0x42 && header[7] == 0x50) // WEBP
            {
                if (read < 30) return null;
                // VP8 chunk: width/height at offset 26 (little-endian, low 14 bits)
                if (header[8] == 0x56 && header[9] == 0x50 && header[10] == 0x38) // VP8
                {
                    var w = BitConverter.ToUInt16(header, 26) & 0x3FFF;
                    var h = BitConverter.ToUInt16(header, 28) & 0x3FFF;
                    if (w > 0 && h > 0) return (w, h);
                }
                // VP8L chunk: width/height at offset 25 (little-endian, encoded)
                if (header[8] == 0x56 && header[9] == 0x50 && header[10] == 0x38 && header[11] == 0x4C) // VP8L
                {
                    if (read < 30) return null;
                    var w = ((header[27] | (header[26] << 8)) + 1);
                    var h = (((header[29] << 4) | (header[28] >> 4)) + 1);
                    if (w > 0 && h > 0) return (w, h);
                }
                // VP8X chunk
                if (header[8] == 0x56 && header[9] == 0x50 && header[10] == 0x38 && header[11] == 0x58) // VP8X
                {
                    if (read < 34) return null;
                    var w = ((header[33] | (header[32] << 8) | (header[31] << 16) | (header[30] << 24)) + 1);
                    var h = ((header[37] | (header[36] << 8) | (header[35] << 16) | (header[34] << 24)) + 1);
                    if (w > 0 && h > 0) return (w, h);
                }
            }

            // GIF: width at offset 6, height at offset 8 (little-endian)
            if (header[0] == 0x47 && header[1] == 0x49 && header[2] == 0x46) // GIF
            {
                if (read < 10) return null;
                return (BitConverter.ToInt16(header, 6), BitConverter.ToInt16(header, 8));
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static readonly int MaxHeaderBytes = 64;

    private static int BigEndianInt32(byte[] data, int offset)
    {
        return (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
    }

    private static (int Width, int Height)? ReadJpegSize(Stream stream)
    {
        // Read through JPEG markers to find SOF (Start of Frame) segment
        var buffer = new byte[MaxHeaderBytes];
        int pos = 2; // Skip SOI (FF D8)

        while (pos < MaxHeaderBytes - 8)
        {
            if (pos >= stream.Length) break;
            stream.Position = pos;
            var read = stream.Read(buffer, 0, Math.Min(buffer.Length, (int)(stream.Length - pos)));
            if (read < 2) return null;

            if (buffer[0] != 0xFF)
            {
                // Not a valid marker, try to find next FF
                var nextFF = -1;
                for (int i = 1; i < read; i++)
                {
                    if (buffer[i] == 0xFF) { nextFF = i; break; }
                }
                if (nextFF < 0) return null;
                pos += nextFF;
                continue;
            }

            var marker = buffer[1];
            if (marker == 0x00 || marker == 0xFF) { pos += 2; continue; } // Padding or stuffed FF

            // SOF markers: C0-C3, C5-C7, C9-CB, CD-CF
            if ((marker >= 0xC0 && marker <= 0xC3) || (marker >= 0xC5 && marker <= 0xC7) ||
                (marker >= 0xC9 && marker <= 0xCB) || (marker >= 0xCD && marker <= 0xCF))
            {
                // SOF segment: length at offset 2-3, height at offset 5-6 (big-endian)
                if (read < 8) return null;
                stream.Position = pos + 4;
                var hBuf = new byte[2];
                if (stream.Read(hBuf, 0, 2) != 2) return null;
                var height = (hBuf[0] << 8) | hBuf[1];

                stream.Position = pos + 6;
                var wBuf = new byte[2];
                if (stream.Read(wBuf, 0, 2) != 2) return null;
                var width = (wBuf[0] << 8) | wBuf[1];

                if (width > 0 && height > 0) return (width, height);
                return null;
            }

            // Skip non-SOF segments (length at offset 2-3)
            if (read < 4) return null;
            var segLen = (buffer[2] << 8) | buffer[3];
            pos += 2 + segLen;
        }

        return null;
    }

    /// <summary>
    /// Formats an image: resize/crop to the target box (unless it is a keep-size request) and save as
    /// PNG or JPEG. Returns the content and the extension to store it under.
    /// </summary>
    public static (byte[] Content, string Extension) FormatImage(byte[] input, MediaFormatOptions options)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("image formatting is only available on Windows hosts");
        }

        using var inputStream = new MemoryStream(input, writable: false);
        using var loaded = Image.FromStream(inputStream, useEmbeddedColorManagement: false, validateImageData: false);

        // Optional cleanup for sources that came from a JPEG download: their 8x8 compression blocks survive a
        // lossless re-encode and show up as a dot/block pattern once the launcher scales the image down.
        var needsCleanup = (options.DeblockAmountPercent > 0 || options.DenoiseAmountPercent > 0)
            && loaded.Width > 2
            && loaded.Height > 2;
        using var cleaned = needsCleanup
            ? DeblockToBitmap(loaded, options.DeblockAmountPercent, options.DenoiseAmountPercent)
            : null;
        var source = (Image?)cleaned ?? loaded;

        // Trust the bytes over the file name: downloads are often saved with a wrong extension, and
        // "keep original" used to follow the (wrong) extension.
        var detected = DetectImageFormat(input);
        var sourceExtension = options.SourceExtension.ToLowerInvariant();
        var sourceIsJpeg = string.Equals(detected, ".jpg", StringComparison.OrdinalIgnoreCase)
            || (detected is null && (sourceExtension == ".jpg" || sourceExtension == ".jpeg"));
        var wantJpeg = options.OutputFormat == "jpeg"
            || (options.OutputFormat == "source" && sourceIsJpeg);
        var extension = wantJpeg ? ".jpg" : ".png";

        var keepSize = options.TargetWidth <= 0 || options.TargetHeight <= 0;
        var targetWidth = keepSize ? source.Width : options.TargetWidth;
        var targetHeight = keepSize ? source.Height : options.TargetHeight;

        using var output = new Bitmap(targetWidth, targetHeight, PixelFormat.Format24bppRgb);
        output.SetResolution(96f, 96f);

        using (var graphics = Graphics.FromImage(output))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.SmoothingMode = SmoothingMode.HighQuality;

            if (!keepSize && options.CropToFill && source.Width > 0 && source.Height > 0)
            {
                // cover: scale so the image fills the box, then center-crop the overflow.
                var scale = Math.Max(targetWidth / (float)source.Width, targetHeight / (float)source.Height);
                var scaledWidth = (int)Math.Ceiling(source.Width * scale);
                var scaledHeight = (int)Math.Ceiling(source.Height * scale);
                var offsetX = (scaledWidth - targetWidth) / 2;
                var offsetY = (scaledHeight - targetHeight) / 2;

                graphics.DrawImage(source, new Rectangle(-offsetX, -offsetY, scaledWidth, scaledHeight));
            }
            else if (!keepSize && options.BlurFillBackground && source.Width > 0 && source.Height > 0)
            {
                // Fit: the WHOLE image is scaled to fit and the leftover space is filled with a blurred,
                // slightly darkened copy of it (the usual "odd aspect" treatment) - nothing gets cropped.
                var backdropWidth = 24;
                var backdropHeight = Math.Max(1, (int)Math.Round(24.0 * targetHeight / targetWidth));
                using (var backdrop = new Bitmap(backdropWidth, backdropHeight, PixelFormat.Format24bppRgb))
                {
                    using (var backdropGraphics = Graphics.FromImage(backdrop))
                    {
                        backdropGraphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        var coverScale = Math.Max(backdropWidth / (float)source.Width, backdropHeight / (float)source.Height);
                        var coverWidth = (int)Math.Ceiling(source.Width * coverScale);
                        var coverHeight = (int)Math.Ceiling(source.Height * coverScale);
                        backdropGraphics.DrawImage(source, new Rectangle((backdropWidth - coverWidth) / 2, (backdropHeight - coverHeight) / 2, coverWidth, coverHeight));
                    }

                    graphics.DrawImage(backdrop, new Rectangle(0, 0, targetWidth, targetHeight));
                }

                using (var shade = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
                {
                    graphics.FillRectangle(shade, 0, 0, targetWidth, targetHeight);
                }

                var fitScale = Math.Min(targetWidth / (float)source.Width, targetHeight / (float)source.Height);
                var fitWidth = Math.Max(1, (int)Math.Round(source.Width * fitScale));
                var fitHeight = Math.Max(1, (int)Math.Round(source.Height * fitScale));
                graphics.DrawImage(source, new Rectangle((targetWidth - fitWidth) / 2, (targetHeight - fitHeight) / 2, fitWidth, fitHeight));
            }
            else
            {
                graphics.DrawImage(source, new Rectangle(0, 0, targetWidth, targetHeight));
            }
        }

        using var memory = new MemoryStream();
        if (wantJpeg)
        {
            var quality = new EncoderParameters(1);
            quality.Param[0] = new EncoderParameter(Encoder.Quality, 92L);
            output.Save(memory, GetEncoder(ImageFormat.Jpeg), quality);
        }
        else
        {
            output.Save(memory, ImageFormat.Png);
        }

        return (memory.ToArray(), extension);
    }

    /// <summary>
    /// Removes the JPEG block grid: a classic deblocking filter that only levels the 8 pixel block boundaries
    /// (where the visible dot/block pattern comes from) and leaves texture, grain and real edges alone. The
    /// step across a boundary is estimated from two pixels on each side (so grain cancels out); boundaries with
    /// a large step are treated as real detail and skipped, and the correction is luminance based so colours
    /// are not shifted.
    /// </summary>
    private static Bitmap DeblockToBitmap(Image source, int amountPercent, int denoisePercent)
    {
        var amount = Math.Clamp(amountPercent, 0, 100) / 100f;
        var denoise = Math.Clamp(denoisePercent, 0, 100) / 100f;
        var width = source.Width;
        var height = source.Height;

        var working = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(working))
        {
            graphics.DrawImage(source, new Rectangle(0, 0, width, height));
        }

        if (amount <= 0 && denoise <= 0)
        {
            return working;
        }

        var data = working.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            var pixels = new int[width * height];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);

            // Two iterations smooth the boundary a touch wider; the strength scales the levelling.
            var passes = amount > 0.7f ? 2 : 1;
            var strength = (0.55f * amount) + 0.25f;

            if (amount > 0)
            {
                for (var pass = 0; pass < passes; pass++)
                {
                    DeblockVerticalBoundaries(pixels, width, height, strength);
                    DeblockHorizontalBoundaries(pixels, width, height, strength);
                }
            }

            if (denoise > 0)
            {
                // Grain smoothing: blend a small blur over texture, but only where the neighbourhood is not a
                // real edge - the same flatness gate the deblock uses, applied over the whole picture.
                SmoothGrain(pixels, width, height, denoise);
            }

            Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
        }
        finally
        {
            working.UnlockBits(data);
        }

        return working;
    }

    /// <summary>Levels the steps at the vertical block edges (x = 8, 16, ...).</summary>
    private static void DeblockVerticalBoundaries(int[] pixels, int width, int height, float strength)
    {
        const int edgeThreshold = 32;

        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            for (var x = 8; x + 1 < width; x += 8)
            {
                var left = (Luma(pixels[row + x - 2]) + Luma(pixels[row + x - 1])) / 2f;
                var right = (Luma(pixels[row + x]) + Luma(pixels[row + x + 1])) / 2f;
                var step = right - left;
                if (Math.Abs(step) >= edgeThreshold)
                {
                    continue;
                }

                var correction = step / 4f * strength;
                Level(pixels, row + x - 1, correction);
                Level(pixels, row + x, -correction);
                Level(pixels, row + x - 2, correction / 2f);
                Level(pixels, row + x + 1, -correction / 2f);
            }
        }
    }

    /// <summary>Levels the steps at the horizontal block edges (y = 8, 16, ...).</summary>
    private static void DeblockHorizontalBoundaries(int[] pixels, int width, int height, float strength)
    {
        const int edgeThreshold = 32;

        for (var y = 8; y + 1 < height; y += 8)
        {
            var aboveA = (y - 2) * width;
            var aboveB = (y - 1) * width;
            var belowA = y * width;
            var belowB = (y + 1) * width;

            for (var x = 0; x < width; x++)
            {
                var above = (Luma(pixels[aboveA + x]) + Luma(pixels[aboveB + x])) / 2f;
                var below = (Luma(pixels[belowA + x]) + Luma(pixels[belowB + x])) / 2f;
                var step = below - above;
                if (Math.Abs(step) >= edgeThreshold)
                {
                    continue;
                }

                var correction = step / 4f * strength;
                Level(pixels, aboveB + x, correction);
                Level(pixels, belowA + x, -correction);
                Level(pixels, aboveA + x, correction / 2f);
                Level(pixels, belowB + x, -correction / 2f);
            }
        }
    }

    private static float Luma(int pixel)
        => (0.299f * ((pixel >> 16) & 0xFF)) + (0.587f * ((pixel >> 8) & 0xFF)) + (0.114f * (pixel & 0xFF));

    /// <summary>Adds a luminance correction to a pixel, keeping the channel differences (so colour is kept).</summary>
    private static void Level(int[] pixels, int index, float correction)
    {
        var pixel = pixels[index];
        var r = (pixel >> 16) & 0xFF;
        var g = (pixel >> 8) & 0xFF;
        var b = pixel & 0xFF;

        var nr = Math.Clamp((int)MathF.Round(r + correction), 0, 255);
        var ng = Math.Clamp((int)MathF.Round(g + correction), 0, 255);
        var nb = Math.Clamp((int)MathF.Round(b + correction), 0, 255);

        pixels[index] = (pixel & unchecked((int)0xFF000000)) | (nr << 16) | (ng << 8) | nb;
    }

    /// <summary>
    /// Softens sensor/JPEG grain while keeping structure: a 3x3 average is blended over each pixel in
    /// proportion to how flat its neighbourhood is, so flat and grainy areas smooth out and edges stay.
    /// </summary>
    private static void SmoothGrain(int[] pixels, int width, int height, float amount)
    {
        var source = new int[pixels.Length];
        Array.Copy(pixels, source, pixels.Length);

        for (var y = 1; y < height - 1; y++)
        {
            var row = y * width;
            for (var x = 1; x < width - 1; x++)
            {
                var index = row + x;
                var centre = source[index];
                var cr = (centre >> 16) & 0xFF;
                var cg = (centre >> 8) & 0xFF;
                var cb = centre & 0xFF;

                var sumR = 0;
                var sumG = 0;
                var sumB = 0;
                var worst = 0;

                for (var dy = -1; dy <= 1; dy++)
                {
                    var neighbourRow = (y + dy) * width;
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var other = source[neighbourRow + x + dx];
                        var r = (other >> 16) & 0xFF;
                        var g = (other >> 8) & 0xFF;
                        var b = other & 0xFF;
                        sumR += r;
                        sumG += g;
                        sumB += b;

                        var difference = Math.Abs(cr - r) + Math.Abs(cg - g) + Math.Abs(cb - b);
                        if (difference > worst)
                        {
                            worst = difference;
                        }
                    }
                }

                // flat / grainy (<= 45) smooths fully, real detail (>= 120) is untouched
                var flatness = worst <= 45 ? 1f : worst >= 120 ? 0f : (120 - worst) / 75f;
                var blend = amount * flatness;

                var nr = (int)MathF.Round(cr + (((sumR / 9f) - cr) * blend));
                var ng = (int)MathF.Round(cg + (((sumG / 9f) - cg) * blend));
                var nb = (int)MathF.Round(cb + (((sumB / 9f) - cb) * blend));

                pixels[index] = (centre & unchecked((int)0xFF000000))
                    | (Math.Clamp(nr, 0, 255) << 16) | (Math.Clamp(ng, 0, 255) << 8) | Math.Clamp(nb, 0, 255);
            }
        }
    }

    /// <summary>
    /// Sniffs an image's real container from its bytes - the file name extension can lie (a JPEG saved as
    /// .png is common for downloaded wallpapers). Returns ".jpg", ".png", ".gif", ".bmp", ".webp" or null.
    /// </summary>
    public static string? DetectImageFormat(ReadOnlySpan<byte> content)
    {
        if (content.Length >= 3 && content[0] == 0xFF && content[1] == 0xD8 && content[2] == 0xFF)
        {
            return ".jpg";
        }

        if (content.Length >= 8 && content[0] == 0x89 && content[1] == 0x50 && content[2] == 0x4E && content[3] == 0x47)
        {
            return ".png";
        }

        if (content.Length >= 6 && content[0] == (byte)'G' && content[1] == (byte)'I' && content[2] == (byte)'F')
        {
            return ".gif";
        }

        if (content.Length >= 2 && content[0] == (byte)'B' && content[1] == (byte)'M')
        {
            return ".bmp";
        }

        if (content.Length >= 12 && content[0] == (byte)'R' && content[1] == (byte)'I' && content[2] == (byte)'F' && content[3] == (byte)'F'
            && content[8] == (byte)'W' && content[9] == (byte)'E' && content[10] == (byte)'B' && content[11] == (byte)'P')
        {
            return ".webp";
        }

        return null;
    }

    /// <summary>True when the data starts with an mp4 (ISO base media) container header.</summary>
    public static bool LooksLikeMp4(ReadOnlySpan<byte> head)
    {
        return head.Length >= 12
            && head[4] == (byte)'f'
            && head[5] == (byte)'t'
            && head[6] == (byte)'y'
            && head[7] == (byte)'p';
    }

    private static ImageCodecInfo GetEncoder(ImageFormat format)
    {
        return ImageCodecInfo.GetImageDecoders().First(codec => codec.FormatID == format.Guid);
    }

    /// <summary>
    /// Optional video formatting via an external ffmpeg (the machine running the server): scales/crops the
    /// clip to the requested size and re-encodes H.264 mp4. When ffmpeg is unavailable or the source is
    /// already the requested size, the clip is returned unchanged with an explanatory note - the caller
    /// always stores valid bytes.
    /// </summary>
    internal static (byte[] Bytes, string? Note) FormatVideo(byte[] input, MediaFormatOptions options, string? ffmpegPath)
    {
        if (options.TargetWidth <= 0 || options.TargetHeight <= 0 || FindFfmpegExecutable(ffmpegPath) is null)
        {
            // No formatting requested/possible - report through the file path so the notes stay identical.
            var (_, note) = FormatVideoFile(null, null, options, ffmpegPath);
            return (input, note);
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "clb-video-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempDir);
            var inFile = Path.Combine(tempDir, "in.mp4");
            var outFile = Path.Combine(tempDir, "out.mp4");
            File.WriteAllBytes(inFile, input);

            var (outputPath, note) = FormatVideoFile(inFile, outFile, options, ffmpegPath);
            return outputPath is null
                ? (input, note)
                : (File.ReadAllBytes(outputPath), note);
        }
        catch (Exception exception)
        {
            return (input, "video formatting failed - stored unchanged (" + exception.Message + ")");
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// File based video formatting (used for uploads - a 2 GB clip never goes through memory): runs ffmpeg
    /// from <paramref name="inputPath"/> to <paramref name="outputPath"/>. Returns the output path on success,
    /// otherwise null plus a note explaining why the clip is used unchanged. The timeout scales with the
    /// input size so large files are not cut off.
    /// </summary>
    internal static (string? OutputPath, string? Note) FormatVideoFile(string? inputPath, string? outputPath, MediaFormatOptions options, string? ffmpegPath)
    {
        if (options.TargetWidth <= 0 || options.TargetHeight <= 0)
        {
            return (null, "video kept at its original size");
        }

        var ffmpeg = FindFfmpegExecutable(ffmpegPath);
        if (ffmpeg is null)
        {
            return (null, "ffmpeg not found - video stored unchanged (set videoFfmpegPath to enable scaling)");
        }

        if (inputPath is null || outputPath is null)
        {
            return (null, "video formatting skipped (no input/output path)");
        }

        try
        {
            var filter = options.CropToFill
                ? $"scale={options.TargetWidth}:{options.TargetHeight}:force_original_aspect_ratio=increase,crop={options.TargetWidth}:{options.TargetHeight}"
                : $"scale={options.TargetWidth}:{options.TargetHeight}";

            var args = $"-y -hide_banner -loglevel error -i \"{inputPath}\" -vf \"{filter}\" "
                + $"-c:v libx264 -preset veryfast -crf 23 -pix_fmt yuv420p -movflags +faststart "
                + $"-c:a aac -b:a 128k \"{outputPath}\"";

            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo(ffmpeg, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                },
            };

            var inputMegabytes = new FileInfo(inputPath).Length / (1024.0 * 1024.0);
            var timeout = TimeSpan.FromMilliseconds(Math.Clamp(120_000 + inputMegabytes * 200, 120_000, 30 * 60_000));

            process.Start();
            process.WaitForExit((int)timeout.TotalMilliseconds);
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return (null, "ffmpeg timed out - video stored unchanged");
            }

            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0 || !LooksLikeMp4File(outputPath))
            {
                return (null, "ffmpeg produced no valid mp4 - video stored unchanged");
            }

            var bytes = new FileInfo(outputPath).Length;
            var sizeText = bytes >= 1024 * 1024
                ? $"{bytes / (1024.0 * 1024.0):F1} MB"
                : $"{bytes / 1024.0:F0} KB";
            return (outputPath, $"formatted to {options.TargetWidth}x{options.TargetHeight} ({sizeText})");
        }
        catch (Exception exception)
        {
            return (null, "video formatting failed - stored unchanged (" + exception.Message + ")");
        }
    }

    /// <summary>True when the file starts with an mp4 (ISO base media) container header.</summary>
    internal static bool LooksLikeMp4File(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var head = new byte[12];
            var read = 0;

            while (read < head.Length)
            {
                var chunk = stream.Read(head, read, head.Length - read);
                if (chunk <= 0)
                {
                    break;
                }

                read += chunk;
            }

            return read >= head.Length && LooksLikeMp4(head);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Locates an ffmpeg executable, in order: the configured path (absolute, or relative to the mod
    /// folder), then common spots inside the mod folder, then the PATH, then C:\ffmpeg style installs.
    /// </summary>
    internal static string? FindFfmpegExecutable(string? configuredPath, string? modDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            if (File.Exists(configuredPath))
            {
                return configuredPath;
            }

            if (!Path.IsPathRooted(configuredPath) && !string.IsNullOrWhiteSpace(modDirectory))
            {
                var relative = Path.Combine(modDirectory, configuredPath);
                if (File.Exists(relative))
                {
                    return relative;
                }
            }
        }

        // Common places players drop the tool inside the mod folder.
        if (!string.IsNullOrWhiteSpace(modDirectory))
        {
            foreach (var candidate in new[]
            {
                Path.Combine(modDirectory, "ffmpeg.exe"),
                Path.Combine(modDirectory, "ffmpeg", "ffmpeg.exe"),
                Path.Combine(modDirectory, "ffmpeg", "bin", "ffmpeg.exe"),
                Path.Combine(modDirectory, "tools", "ffmpeg", "ffmpeg.exe"),
                Path.Combine(modDirectory, "tools", "ffmpeg", "bin", "ffmpeg.exe"),
            })
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            foreach (var candidate in new[] { Path.Combine(directory, "ffmpeg.exe"), Path.Combine(directory, "ffmpeg") })
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        foreach (var candidate in new[] { @"C:\ffmpeg\bin\ffmpeg.exe", @"C:\Program Files\ffmpeg\bin\ffmpeg.exe" })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
