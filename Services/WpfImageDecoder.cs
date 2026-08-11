using System.IO;
using System.Windows.Media.Imaging;

namespace CustomImageViewer.Services;

public sealed class WpfImageDecoder : IImageDecoder
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bmp", ".dib", ".gif", ".ico", ".jfif", ".jpe", ".jpeg", ".jpg",
        ".png", ".tif", ".tiff", ".wdp", ".jxr", ".webp"
    };

    public bool CanDecode(string filePath) => Extensions.Contains(Path.GetExtension(filePath));

    public Task<BitmapSource?> LoadAsync(string filePath, int? decodePixelWidth, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
                if (decodePixelWidth is > 0) image.DecodePixelWidth = decodePixelWidth.Value;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                return (BitmapSource?)image;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }, cancellationToken);
}
