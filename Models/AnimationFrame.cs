using System.Windows.Media.Imaging;

namespace CustomImageViewer.Models;

public sealed record AnimationFrame(BitmapSource Image, TimeSpan Duration);

public sealed record DecodedAnimation(IReadOnlyList<AnimationFrame> Frames, uint Iterations)
{
    public bool IsAnimated => Frames.Count > 1;
}
