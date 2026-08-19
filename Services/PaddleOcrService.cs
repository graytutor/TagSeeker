using OpenCvSharp;
using Sdcb.PaddleInference;
using Sdcb.PaddleOCR;
using Sdcb.PaddleOCR.Models.Local;
using System.Text;

namespace CustomImageViewer.Services;

/// <summary>
/// Bundled PP-OCRv4 engine. It runs entirely in this process and does not need a
/// server, account, or model download after the application has been installed.
/// </summary>
public sealed class PaddleOcrService : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private PaddleOcrAll? _japaneseEngine;
    private PaddleOcrAll? _chineseEngine;

    public async Task<OcrTextResult> RecognizeAsync(string imagePath, CancellationToken cancellationToken)
    {
        try
        {
            await _gate.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return EmptyResult();
        }

        try
        {
            try
            {
                return await Task.Run(() => RecognizeCore(imagePath, cancellationToken), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return EmptyResult();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private OcrTextResult RecognizeCore(string imagePath, CancellationToken cancellationToken)
    {
        try
        {
            if (cancellationToken.IsCancellationRequested) return EmptyResult();
            // Paddle 3.x's PP-OCRv5 PIR model currently fails in the Windows
            // oneDNN predictor. The stable 2.5 runtime + PP-OCRv4 model avoids
            // that converter while retaining local Chinese/Japanese OCR.
            _japaneseEngine ??= new PaddleOcrAll(LocalFullModels.JapanV4, PaddleDevice.Mkldnn())
            {
                AllowRotateDetection = true,
                // Viewer images are already upright. Skipping the per-region
                // upside-down classifier considerably reduces page OCR time.
                Enable180Classification = false
            };
            using var image = Cv2.ImRead(imagePath, ImreadModes.Color);
            if (image.Empty()) return EmptyResult();

            // Japanese must be recognized with the Japanese dictionary. The
            // Chinese model maps kana to unrelated Han characters, leaving the
            // translator with irrecoverably damaged source text.
            var japanese = BuildTextResult(_japaneseEngine.Run(image, recognizeBatchSize: 8));
            if (cancellationToken.IsCancellationRequested) return EmptyResult();

            if (CountKana(japanese.Text) >= 2)
                return japanese;

            // Kana-free pages are commonly Chinese. Only those pages pay for a
            // second recognition pass, keeping normal Japanese manga responsive.
            _chineseEngine ??= new PaddleOcrAll(LocalFullModels.ChineseV4, PaddleDevice.Mkldnn())
            {
                AllowRotateDetection = true,
                Enable180Classification = false
            };
            var chinese = BuildTextResult(_chineseEngine.Run(image, recognizeBatchSize: 8));
            if (cancellationToken.IsCancellationRequested) return EmptyResult();
            return string.IsNullOrWhiteSpace(chinese.Text) ? japanese : chinese;
        }
        catch (OperationCanceledException)
        {
            return EmptyResult();
        }
        catch
        {
            // Do not let a native predictor failure escape the worker task. Apart
            // from presenting an intrusive debugger break, a faulted native engine
            // cannot safely be reused. Returning an empty result activates the
            // Windows OCR fallback in MainWindow.
            _japaneseEngine?.Dispose();
            _japaneseEngine = null;
            _chineseEngine?.Dispose();
            _chineseEngine = null;
            return EmptyResult();
        }
    }

    private static OcrTextResult BuildTextResult(PaddleOcrResult result)
    {
        var lines = result.Regions
                .Where(region => region.Score >= 0.30f && !string.IsNullOrWhiteSpace(region.Text))
                .Select(region =>
                {
                    var points = region.Rect.Points();
                    var left = points.Min(point => (double)point.X);
                    var top = points.Min(point => (double)point.Y);
                    var right = points.Max(point => (double)point.X);
                    var bottom = points.Max(point => (double)point.Y);
                    return new OcrLineRegion(
                        region.Text.Trim(), left, top,
                        Math.Max(1, right - left), Math.Max(1, bottom - top),
                        1, Math.Max(1, bottom - top));
                })
                .OrderBy(line => line.Y)
                .ThenBy(line => line.X)
                .ToList();

        var blocks = GroupNearbyLines(lines);
        var text = string.Join(Environment.NewLine + Environment.NewLine, blocks.Select(block => block.Text));
        return new OcrTextResult(text, DetectLanguage(text), blocks);
    }

    private static OcrTextResult EmptyResult() => new(string.Empty, "und", []);

    private static string DetectLanguage(string text)
    {
        var kana = 0;
        var hangul = 0;
        var han = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var value = rune.Value;
            if (value is >= 0x3040 and <= 0x30FF) kana++;
            else if (value is >= 0xAC00 and <= 0xD7AF || value is >= 0x1100 and <= 0x11FF) hangul++;
            else if (value is >= 0x3400 and <= 0x9FFF) han++;
        }
        if (kana > 0) return "ja";
        if (hangul > 0) return "ko";
        if (han > 0) return "zh-Hans";
        return "en";
    }

    private static int CountKana(string text) => text.EnumerateRunes().Count(rune =>
        rune.Value is >= 0x3040 and <= 0x30FF);

    private static IReadOnlyList<OcrLineRegion> GroupNearbyLines(IReadOnlyList<OcrLineRegion> lines)
    {
        var groups = new List<List<OcrLineRegion>>();
        foreach (var line in lines)
        {
            var target = groups
                .Where(group => CanJoin(group, line))
                .OrderBy(group => Math.Abs(line.Y - group.Max(item => item.Y + item.Height)))
                .FirstOrDefault();
            if (target is null) groups.Add([line]); else target.Add(line);
        }

        var blocks = groups.Select(group =>
        {
            var ordered = group.OrderBy(line => line.Y).ThenBy(line => line.X).ToList();
            var left = ordered.Min(line => line.X);
            var top = ordered.Min(line => line.Y);
            var right = ordered.Max(line => line.X + line.Width);
            var bottom = ordered.Max(line => line.Y + line.Height);
            return new OcrLineRegion(
                string.Join(Environment.NewLine, ordered.Select(line => line.Text)),
                left, top, right - left, bottom - top, ordered.Count,
                ordered.Average(line => line.TypicalLineHeight));
        }).ToList();

        // Japanese vertical text is read from the rightmost column to the left.
        // Paddle returns those columns as tall regions; ordering them by Y/X would
        // reverse the sentence and severely damage translation quality.
        var verticalCount = blocks.Count(block => block.Height > block.Width * 1.6);
        var mostlyVertical = verticalCount >= 2 && verticalCount >= blocks.Count * 0.6;
        var mergedBlocks = MergeAdjacentVerticalColumns(blocks);
        return mostlyVertical
            ? mergedBlocks.OrderByDescending(block => block.X).ThenBy(block => block.Y).ToList()
            : mergedBlocks.OrderBy(block => block.Y).ThenBy(block => block.X).ToList();
    }

    internal static IReadOnlyList<OcrLineRegion> MergeAdjacentVerticalColumns(
        IReadOnlyList<OcrLineRegion> blocks)
    {
        var vertical = blocks
            .Where(IsVertical)
            .OrderByDescending(block => block.X)
            .ToList();
        var output = blocks.Where(block => !IsVertical(block)).ToList();

        while (vertical.Count > 0)
        {
            var group = new List<OcrLineRegion> { vertical[0] };
            vertical.RemoveAt(0);
            while (true)
            {
                var candidate = vertical
                    .Where(item => CanJoinVerticalColumns(group, item))
                    .OrderBy(item => HorizontalDistance(group, item))
                    .FirstOrDefault();
                if (candidate is null) break;
                group.Add(candidate);
                vertical.Remove(candidate);
            }

            var ordered = group.OrderByDescending(item => item.X).ThenBy(item => item.Y).ToList();
            var left = ordered.Min(item => item.X);
            var top = ordered.Min(item => item.Y);
            var right = ordered.Max(item => item.X + item.Width);
            var bottom = ordered.Max(item => item.Y + item.Height);
            output.Add(new OcrLineRegion(
                string.Concat(ordered.Select(item =>
                    item.Text.Replace("\r", string.Empty).Replace("\n", string.Empty))),
                left, top, right - left, bottom - top,
                ordered.Sum(item => Math.Max(1, item.LineCount)),
                ordered.Average(item => item.TypicalLineHeight)));
        }

        return output;
    }

    private static bool CanJoinVerticalColumns(
        IReadOnlyList<OcrLineRegion> group,
        OcrLineRegion candidate)
    {
        if (!IsVertical(candidate)) return false;
        var left = group.Min(item => item.X);
        var right = group.Max(item => item.X + item.Width);
        var top = group.Min(item => item.Y);
        var bottom = group.Max(item => item.Y + item.Height);
        var overlap = Math.Max(0,
            Math.Min(bottom, candidate.Y + candidate.Height) - Math.Max(top, candidate.Y));
        var overlapRatio = overlap / Math.Max(1, Math.Min(bottom - top, candidate.Height));
        var horizontalGap = candidate.X + candidate.Width < left
            ? left - (candidate.X + candidate.Width)
            : candidate.X > right
                ? candidate.X - right
                : 0;
        var typicalWidth = Math.Max(
            group.Average(item => item.Width),
            candidate.Width);

        // Columns in the same Japanese speech balloon normally overlap strongly
        // on the Y axis and are separated by no more than roughly one character.
        return overlapRatio >= 0.45 && horizontalGap <= Math.Max(8, typicalWidth * 1.35);
    }

    private static double HorizontalDistance(
        IReadOnlyList<OcrLineRegion> group,
        OcrLineRegion candidate)
    {
        var left = group.Min(item => item.X);
        var right = group.Max(item => item.X + item.Width);
        if (candidate.X + candidate.Width < left) return left - (candidate.X + candidate.Width);
        return candidate.X > right ? candidate.X - right : 0;
    }

    private static bool IsVertical(OcrLineRegion block) => block.Height > block.Width * 1.6;

    private static bool CanJoin(IReadOnlyList<OcrLineRegion> group, OcrLineRegion line)
    {
        var left = group.Min(item => item.X);
        var right = group.Max(item => item.X + item.Width);
        var bottom = group.Max(item => item.Y + item.Height);
        var averageHeight = group.Average(item => item.Height);
        var overlap = Math.Max(0, Math.Min(right, line.X + line.Width) - Math.Max(left, line.X));
        var overlapRatio = overlap / Math.Max(1, Math.Min(right - left, line.Width));
        var gap = Math.Max(0, line.Y - bottom);
        var vertical = line.Height > line.Width * 1.6 || group.Any(item => item.Height > item.Width * 1.6);
        return !vertical && gap <= Math.Max(averageHeight, line.Height) * 2.0 && overlapRatio >= 0.18;
    }

    public void Dispose()
    {
        _japaneseEngine?.Dispose();
        _chineseEngine?.Dispose();
        _gate.Dispose();
    }
}
