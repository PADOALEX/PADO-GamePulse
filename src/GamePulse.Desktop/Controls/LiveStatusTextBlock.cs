using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace GamePulse.Desktop.Controls;

/// <summary>
/// Uses the normal text automation peer while suppressing live announcements from hidden pages.
/// Avalonia 12.1's Win32 provider emits LiveRegionChanged when a live peer's name changes.
/// The framework's live-setting implementation does not itself consider ancestor visibility.
/// </summary>
public sealed class LiveStatusTextBlock : TextBlock
{
    protected override Type StyleKeyOverride => typeof(TextBlock);

    protected override AutomationPeer OnCreateAutomationPeer() => new LiveStatusAutomationPeer(this);

    private sealed class LiveStatusAutomationPeer(LiveStatusTextBlock owner) : TextBlockAutomationPeer(owner)
    {
        protected override AutomationLiveSetting GetLiveSettingCore() =>
            Owner.IsEffectivelyVisible && TopLevel.GetTopLevel(Owner) is { IsEffectivelyVisible: true }
                ? base.GetLiveSettingCore() : AutomationLiveSetting.Off;
    }
}
