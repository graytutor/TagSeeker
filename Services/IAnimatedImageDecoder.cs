using CustomImageViewer.Models;

namespace CustomImageViewer.Services;

public interface IAnimatedImageDecoder
{
    bool CanDecodeAnimation(string filePath);
    Task<DecodedAnimation> LoadAnimationAsync(string filePath, CancellationToken cancellationToken);
}
