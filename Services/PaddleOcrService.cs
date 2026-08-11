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
    private PaddleOcrAll? _engine;

    public async Task<OcrTextResult> RecognizeAsync(string imagePath, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() => RecognizeCore(imagePath, cancellationToken), cancellationToken);
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
            cancellationToken.ThrowIfCancellationRequested();
            // Paddle 3.x's PP-OCRv5 PIR model currently fails in the Windows
            // oneDNN predictor. The stable 2.5 runtime + PP-OCRv4 model avoids
            // that converter while retaining local Chinese/Japanese OCR.
            _engine ??= new PaddleOcrAll(LocalFullModels.ChineseV4, PaddleDevice.Mkldnn())
            {
                AllowRotateDetection = true,
                // Viewer images are already upright. Skipping the per-region
                // upside-down classifier considerably reduces page OCR time.
                Enable180Classification = false
            };
            using var image = Cv2.ImRead(imagePath, ImreadModes.Color);
            if (image.Empty()) return EmptyResult();

            // Recognize several detected text crops in one predictor call. The
            // default single-crop path is unnecessarily slow on text-heavy pages.
            var result = _engine.Run(image, recognizeBatchSize: 8);
            cancellationToken.ThrowIfCancellationRequested();
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
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Do not let a native predictor failure escape the worker task. Apart
            // from presenting an intrusive debugger break, a faulted native engine
            // cannot safely be reused. Returning an empty result activates the
            // Windows OCR fallback in MainWindow.
            _engine?.Dispose();
            _engine = null;
            return EmptyResult();
        }
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

        return groups.Select(group =>
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
        }).OrderBy(block => block.Y).ThenBy(block => block.X).ToList();
    }

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
        _engine?.Dispose();
        _gate.Dispose();
    }
}
