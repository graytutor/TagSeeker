using System.IO;
using System.Windows.Media.Imaging;

namespace CustomImageViewer.Services;

public sealed class CompositeImageDecoder(params IImageDecoder[] decoders) : IImageDecoder
{
    private readonly IReadOnlyList<IImageDecoder> _decoders = decoders;

    public bool CanDecode(string filePath) => _decoders.Any(x => x.CanDecode(filePath));

    public async Task<BitmapSource?> LoadAsync(string filePath, int? decodePixelWidth, CancellationToken cancellationToken)
    {
        foreach (var decoder in _decoders.Where(x => x.CanDecode(filePath)))
        {
            if (cancellationToken.IsCancellationRequested) return null;
            BitmapSource? image;
            try
            {
                image = await decoder.LoadAsync(filePath, decodePixelWidth, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            if (image is not null) return image;
        }

        return null;
    }
}
