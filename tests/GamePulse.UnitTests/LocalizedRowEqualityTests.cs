using GamePulse.Core;
using GamePulse.Desktop.ViewModels;
using Xunit;

namespace GamePulse.UnitTests;

public sealed class LocalizedRowEqualityTests
{
    [Fact]
    public void Inventory_row_equality_and_hash_ignore_binding_subscriptions_but_keep_source_fields()
    {
        var item = new InventoryItem("101", "201", "0", 2, "Test item", "Fixture", null, true, false, true);
        var attached = new InventoryRow(item);
        var hash = attached.GetHashCode();
        var notifications = 0;
        attached.PropertyChanged += (_, _) => notifications++;
        attached.RefreshLocalization();

        Assert.Equal(1, notifications);
        Assert.Equal(hash, attached.GetHashCode());
        Assert.Equal(attached, new InventoryRow(item));
        Assert.NotEqual(attached, new InventoryRow(item with { Amount = 3 }));
    }

    [Fact]
    public void Friend_row_equality_and_hash_ignore_binding_subscriptions_but_keep_source_fields()
    {
        var friend = new SteamFriend("76561198000000001", null, null, DataState.Unavailable);
        var attached = new FriendRow(friend);
        var hash = attached.GetHashCode();
        attached.PropertyChanged += (_, _) => { };

        Assert.Equal(hash, attached.GetHashCode());
        Assert.Equal(attached, new FriendRow(friend));
        Assert.NotEqual(attached, new FriendRow(friend with { SummaryState = DataState.Failed }));
        Assert.False(((LocalizedRecord)attached).Equals(new InventoryRow(
            new("101", "201", "0", 1, "Test item", null, null, null, null, false))));
    }
}
