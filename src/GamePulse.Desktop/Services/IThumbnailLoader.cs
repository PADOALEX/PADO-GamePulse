using Avalonia.Media.Imaging;

namespace GamePulse.Desktop.Services;

/// <summary>Loads an optional image. The caller owns each returned bitmap.</summary>
public interface IThumbnailLoader
{
    Task<Bitmap?> LoadAsync(string? url, CancellationToken token);
}
