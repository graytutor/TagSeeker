using System.Windows.Media.Imaging;

namespace CustomImageViewer.Services;

public interface IImageDecoder
{
    bool CanDecode(string filePath);
    /// <summary>Returns null when the file is not a valid image for this decoder.</summary>
    Task<BitmapSource?> LoadAsync(string filePath, int? decodePixelWidth, CancellationToken cancellationToken);
}
