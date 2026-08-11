using ImageMagick;
using System.IO;
using System.Text;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;

namespace CustomImageViewer.Services;

public sealed class WindowsOcrService
{
    public IReadOnlyList<OcrLanguageOption> GetAvailableLanguages() => OcrEngine.AvailableRecognizerLanguages
        .Select(language => new OcrLanguageOption(language.LanguageTag, language.DisplayName))
        .OrderBy(language => language.DisplayName, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    public async Task<OcrTextResult> RecognizeAsync(
        string imagePath,
        string? languageTag,
        CancellationToken cancellationToken)
    {
        var file = await StorageFile.GetFileFromPathAsync(imagePath);
        using var stream = await file.OpenAsync(FileAccessMode.Read);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        using var bitmap = await DecodeWithinOcrLimitAsync(decoder, OcrEngine.MaxImageDimension);

        var languages = string.IsNullOrWhiteSpace(languageTag)
            ? OcrEngine.AvailableRecognizerLanguages.ToList()
            : [new Language(languageTag)];
        if (languages.Count == 0)
            throw new InvalidOperationException("설치된 Windows OCR 언어가 없습니다.");

        OcrTextResult? best = null;
        var bestScore = double.MinValue;
        foreach (var language in languages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var engine = OcrEngine.TryCreateFromLanguage(language);
            if (engine is null) continue;
            var result = await engine.RecognizeAsync(bitmap);
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = BuildTextResult(result, language.LanguageTag, out var score);
            if (score <= bestScore) continue;
            bestScore = score;
            best = candidate;
        }

        // Once the likely language is known, try contrast-enhanced variants with only
        // that engine. This improves text whose color is close to the background.
        if (best is not null)
        {
            var engine = OcrEngine.TryCreateFromLanguage(new Language(best.RecognizedLanguageTag));
            if (engine is not null)
            {
                foreach (var enhancement in Enum.GetValues<OcrEnhancement>())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var enhancedBitmap = await CreateEnhancedBitmapAsync(imagePath, enhancement, cancellationToken);
                    var result = await engine.RecognizeAsync(enhancedBitmap);
                    var candidate = BuildTextResult(result, best.RecognizedLanguageTag, out var score);
                    if (score <= bestScore) continue;
                    bestScore = score;
                    best = candidate;
                }
            }
        }

        return best ?? new OcrTextResult(string.Empty, languages[0].LanguageTag, []);
    }

    private static OcrTextResult BuildTextResult(OcrResult result, string languageTag, out double score)
    {
        var lines = result.Lines
            .Where(line => !string.IsNullOrWhiteSpace(line.Text) && line.Words.Count > 0)
            .Select(line =>
            {
                var left = line.Words.Min(word => word.BoundingRect.X);
                var top = line.Words.Min(word => word.BoundingRect.Y);
                var right = line.Words.Max(word => word.BoundingRect.X + word.BoundingRect.Width);
                var bottom = line.Words.Max(word => word.BoundingRect.Y + word.BoundingRect.Height);
                return new OcrLineRegion(line.Text, left, top, right - left, bottom - top, 1, bottom - top);
            })
            .ToList();
        var rawText = string.Join(Environment.NewLine, lines.Select(line => line.Text));
        score = ScoreRecognition(rawText, languageTag);
        var blocks = GroupNearbyLines(lines);
        var text = string.Join(Environment.NewLine + Environment.NewLine, blocks.Select(block => block.Text));
        return new OcrTextResult(text, languageTag, blocks);
    }

    private static async Task<SoftwareBitmap> CreateEnhancedBitmapAsync(
        string imagePath,
        OcrEnhancement enhancement,
        CancellationToken cancellationToken)
    {
        var tempFolder = Path.Combine(Path.GetTempPath(), "CustomImageViewer", "ocr");
        Directory.CreateDirectory(tempFolder);
        var tempPath = Path.Combine(tempFolder, $"{Guid.NewGuid():N}.png");
        try
        {
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var image = new MagickImage(imagePath);
                image.AutoOrient();
                image.ColorSpace = ColorSpace.Gray;
                image.ContrastStretch(new Percentage(2), new Percentage(2));
                image.Sharpen(0, 1);
                if (enhancement is OcrEnhancement.AdaptiveThreshold or OcrEnhancement.InvertedAdaptiveThreshold)
                    image.AdaptiveThreshold(31, 31, new Percentage(5));
                if (enhancement == OcrEnhancement.InvertedAdaptiveThreshold)
                    image.Negate();
                image.Write(tempPath, MagickFormat.Png);
            }, cancellationToken);

            var file = await StorageFile.GetFileFromPathAsync(tempPath);
            using var stream = await file.OpenAsync(FileAccessMode.Read);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            return await DecodeWithinOcrLimitAsync(decoder, OcrEngine.MaxImageDimension);
        }
        finally
        {
            try { File.Delete(tempPath); } catch { }
        }
    }

    private static double ScoreRecognition(string text, string languageTag)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var score = 0.0;
        var tag = languageTag.ToLowerInvariant();
        foreach (var rune in text.EnumerateRunes())
        {
            var value = rune.Value;
            if (Rune.IsLetterOrDigit(rune)) score += 2;
            else if (Rune.IsWhiteSpace(rune)) score += 0.1;
            else if (value is 0xFFFD or 0x25A1) score -= 8;

            var isHangul = value is >= 0xAC00 and <= 0xD7AF || value is >= 0x1100 and <= 0x11FF;
            var isKana = value is >= 0x3040 and <= 0x30FF;
            var isHan = value is >= 0x3400 and <= 0x9FFF;
            var isCyrillic = value is >= 0x0400 and <= 0x052F;
            var isArabic = value is >= 0x0600 and <= 0x06FF;

            if (tag.StartsWith("ko") && isHangul) score += 7;
            if (tag.StartsWith("ja") && isKana) score += 9;
            if (tag.StartsWith("ja") && isHan) score += 2;
            if (tag.StartsWith("zh") && isHan) score += 5;
            if ((tag.StartsWith("ru") || tag.StartsWith("uk")) && isCyrillic) score += 6;
            if (tag.StartsWith("ar") && isArabic) score += 6;

            if (!tag.StartsWith("ko") && isHangul) score -= 2;
            if (!tag.StartsWith("ja") && isKana) score -= 2;
        }
        return score;
    }

    private static IReadOnlyList<OcrLineRegion> GroupNearbyLines(IReadOnlyList<OcrLineRegion> lines)
    {
        var groups = new List<List<OcrLineRegion>>();
        foreach (var line in lines.OrderBy(line => line.Y).ThenBy(line => line.X))
        {
            var target = groups
                .Where(group => CanJoin(group, line))
                .OrderBy(group => VerticalGap(group, line))
                .FirstOrDefault();
            if (target is null) groups.Add([line]); else target.Add(line);
        }

        return groups
            .Select(group =>
            {
                var ordered = group.OrderBy(line => line.Y).ThenBy(line => line.X).ToList();
                var left = ordered.Min(line => line.X);
                var top = ordered.Min(line => line.Y);
                var right = ordered.Max(line => line.X + line.Width);
                var bottom = ordered.Max(line => line.Y + line.Height);
                return new OcrLineRegion(
                    string.Join(Environment.NewLine, ordered.Select(line => line.Text)),
                    left, top, right - left, bottom - top,
                    ordered.Count,
                    ordered.Average(line => line.TypicalLineHeight));
            })
            .OrderBy(block => block.Y)
            .ThenBy(block => block.X)
            .ToList();
    }

    private static bool CanJoin(IReadOnlyList<OcrLineRegion> group, OcrLineRegion line)
    {
        var left = group.Min(item => item.X);
        var right = group.Max(item => item.X + item.Width);
        var bottom = group.Max(item => item.Y + item.Height);
        var averageHeight = group.Average(item => item.Height);
        var groupWidth = right - left;
        var overlap = Math.Max(0, Math.Min(right, line.X + line.Width) - Math.Max(left, line.X));
        var overlapRatio = overlap / Math.Max(1, Math.Min(groupWidth, line.Width));
        var gap = Math.Max(0, line.Y - bottom);
        var verticalLine = line.Height > line.Width * 1.6;
        var verticalGroup = group.Any(item => item.Height > item.Width * 1.6);
        var groupCenter = (left + right) / 2;
        var lineCenter = line.X + line.Width / 2;
        var centersAreNear = Math.Abs(groupCenter - lineCenter) <= Math.Max(groupWidth, line.Width) * 0.7;
        return !verticalLine && !verticalGroup
            && gap <= Math.Max(averageHeight, line.Height) * 2.3
            && (overlapRatio >= 0.12 || centersAreNear);
    }

    private static double VerticalGap(IReadOnlyList<OcrLineRegion> group, OcrLineRegion line) =>
        Math.Abs(line.Y - group.Max(item => item.Y + item.Height));

    private static async Task<SoftwareBitmap> DecodeWithinOcrLimitAsync(BitmapDecoder decoder, uint maxDimension)
    {
        var width = decoder.PixelWidth;
        var height = decoder.PixelHeight;
        var transform = new BitmapTransform();
        if (Math.Max(width, height) > maxDimension)
        {
            var scale = (double)maxDimension / Math.Max(width, height);
            transform.ScaledWidth = Math.Max(1, (uint)Math.Round(width * scale));
            transform.ScaledHeight = Math.Max(1, (uint)Math.Round(height * scale));
        }

        return await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.ColorManageToSRgb);
    }
}

public sealed record OcrLanguageOption(string LanguageTag, string DisplayName);
public sealed record OcrTextResult(string Text, string RecognizedLanguageTag, IReadOnlyList<OcrLineRegion> Lines);
public sealed record OcrLineRegion(
    string Text,
    double X,
    double Y,
    double Width,
    double Height,
    int LineCount,
    double TypicalLineHeight);

public enum OcrEnhancement
{
    Contrast,
    AdaptiveThreshold,
    InvertedAdaptiveThreshold
}
