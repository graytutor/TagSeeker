using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CustomImageViewer.Services;

/// <summary>24/32-bit, uncompressed or RLE true-color TGA decoder.</summary>
public sealed class TgaImageDecoder : IImageDecoder
{
    public bool CanDecode(string filePath) => Path.GetExtension(filePath).Equals(".tga", StringComparison.OrdinalIgnoreCase);

    public Task<BitmapSource?> LoadAsync(string filePath, int? decodePixelWidth, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            try
            {
                return (BitmapSource?)Decode(filePath, decodePixelWidth, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is NotSupportedException or EndOfStreamException or IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }, cancellationToken);

    private static BitmapSource Decode(string path, int? decodePixelWidth, CancellationToken token)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new BinaryReader(stream);
        var idLength = reader.ReadByte();
        var colorMapType = reader.ReadByte();
        var imageType = reader.ReadByte();
        reader.ReadBytes(5);
        reader.ReadUInt16();
        reader.ReadUInt16();
        var width = reader.ReadUInt16();
        var height = reader.ReadUInt16();
        var bitsPerPixel = reader.ReadByte();
        var descriptor = reader.ReadByte();

        if (colorMapType != 0 || imageType is not (2 or 10) || bitsPerPixel is not (24 or 32) || width == 0 || height == 0)
            throw new NotSupportedException("현재 TGA 디코더는 24/32비트 True Color(비압축/RLE) 형식을 지원합니다.");

        reader.ReadBytes(idLength);
        var bytesPerPixel = bitsPerPixel / 8;
        var pixelCount = width * height;
        var source = new byte[pixelCount * bytesPerPixel];

        if (imageType == 2)
        {
            var read = reader.Read(source, 0, source.Length);
            if (read != source.Length) throw new EndOfStreamException();
        }
        else
        {
            var outputPixel = 0;
            while (outputPixel < pixelCount)
            {
                token.ThrowIfCancellationRequested();
                var header = reader.ReadByte();
                var count = (header & 0x7F) + 1;
                if ((header & 0x80) != 0)
                {
                    var pixel = reader.ReadBytes(bytesPerPixel);
                    for (var i = 0; i < count; i++)
                    {
                        Buffer.BlockCopy(pixel, 0, source, outputPixel * bytesPerPixel, bytesPerPixel);
                        outputPixel++;
                    }
                }
                else
                {
                    var size = count * bytesPerPixel;
                    var block = reader.ReadBytes(size);
                    Buffer.BlockCopy(block, 0, source, outputPixel * bytesPerPixel, size);
                    outputPixel += count;
                }
            }
        }

        token.ThrowIfCancellationRequested();
        var topOrigin = (descriptor & 0x20) != 0;
        var rightOrigin = (descriptor & 0x10) != 0;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var sourceY = topOrigin ? y : height - 1 - y;
            var sourceX = rightOrigin ? width - 1 - x : x;
            var sourceOffset = (sourceY * width + sourceX) * bytesPerPixel;
            var targetOffset = y * stride + x * 4;
            pixels[targetOffset] = source[sourceOffset];
            pixels[targetOffset + 1] = source[sourceOffset + 1];
            pixels[targetOffset + 2] = source[sourceOffset + 2];
            pixels[targetOffset + 3] = bytesPerPixel == 4 ? source[sourceOffset + 3] : (byte)255;
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        if (decodePixelWidth is > 0 && width > decodePixelWidth.Value)
        {
            var scaled = new TransformedBitmap(bitmap, new ScaleTransform((double)decodePixelWidth.Value / width, (double)decodePixelWidth.Value / width));
            scaled.Freeze();
            return scaled;
        }

        bitmap.Freeze();
        return bitmap;
    }
}
