using Avalonia;
using Avalonia.Controls;

namespace GamePulse.Desktop.Controls;

/// <summary>Keep filters scrollable while reserving a finite viewport for virtualized results.</summary>
public sealed class AdaptivePageGrid : Grid
{
    public double BodyReserve { get; set; } = 150;
    public double HeaderFraction { get; set; } = 0.55;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.FirstOrDefault() is ScrollViewer header && double.IsFinite(availableSize.Height))
            header.MaxHeight = Math.Max(48, Math.Min(availableSize.Height * HeaderFraction,
                availableSize.Height - BodyReserve - RowSpacing * 2 - 30));
        return base.MeasureOverride(availableSize);
    }
}
