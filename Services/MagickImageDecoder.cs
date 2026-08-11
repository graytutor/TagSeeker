using CustomImageViewer.Models;
using ImageMagick;
using System.IO;
using System.Windows.Media.Imaging;

namespace CustomImageViewer.Services;

/// <summary>
/// Decoder for formats that WPF cannot reliably decode on every PC, and for animated images.
/// Add extensions here without changing the explorer or viewer.
/// </summary>
public sealed class MagickImageDecoder : IImageDecoder, IAnimatedImageDecoder
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".avif", ".bmp", ".dib", ".gif", ".heic", ".heif", ".ico", ".jfif",
        ".jpe", ".jpeg", ".jpg", ".png", ".tga", ".tif", ".tiff", ".webp"
    };

    private static readonly HashSet<string> AnimatedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".gif", ".webp"
    };

    public bool CanDecode(string filePath) => Extensions.Contains(Path.GetExtension(filePath));

    public bool CanDecodeAnimation(string filePath) => AnimatedExtensions.Contains(Path.GetExtension(filePath));

    public Task<BitmapSource?> LoadAsync(string filePath, int? decodePixelWidth, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            try
            {
                if (cancellationToken.IsCancellationRequested) return null;
                using var image = new MagickImage(filePath);
                if (cancellationToken.IsCancellationRequested) return null;
                image.AutoOrient();
                if (decodePixelWidth is > 0 && image.Width > decodePixelWidth.Value)
                    image.Resize((uint)decodePixelWidth.Value, 0);
                return (BitmapSource?)ToBitmapSource(image);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex) when (ex is MagickException or IOException or UnauthorizedAccessException)
            {
                return null;
            }
        });

    public Task<DecodedAnimation> LoadAnimationAsync(string filePath, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            try
            {
                if (cancellationToken.IsCancellationRequested)
                    return new DecodedAnimation([], 0);
                using var images = new MagickImageCollection(filePath);
                if (cancellationToken.IsCancellationRequested)
                    return new DecodedAnimation([], 0);

                // Animated frames often store only the changed region. Coalesce produces full frames.
                images.Coalesce();
                var frames = new List<AnimationFrame>(images.Count);
                uint iterations = 0;

                foreach (var image in images)
                {
                    if (cancellationToken.IsCancellationRequested)
                        return new DecodedAnimation([], 0);
                    iterations = image.AnimationIterations;
                    var hundredths = Math.Max(1, image.AnimationDelay);
                    frames.Add(new AnimationFrame(ToBitmapSource(image), TimeSpan.FromMilliseconds(hundredths * 10.0)));
                }

                return new DecodedAnimation(frames, iterations);
            }
            catch (OperationCanceledException)
            {
                return new DecodedAnimation([], 0);
            }
            catch (Exception ex) when (ex is MagickException or IOException or UnauthorizedAccessException)
            {
                return new DecodedAnimation([], 0);
            }
        });

    private static BitmapSource ToBitmapSource(IMagickImage<byte> image)
    {
        using var stream = new MemoryStream();
        image.Write(stream, MagickFormat.Png);
        stream.Position = 0;

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
