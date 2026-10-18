using System.Collections;
using GamePulse.Core;
using Xunit;

namespace GamePulse.UnitTests;

public sealed class SortingCancellationTests
{
    [Fact]
    public void Friend_sort_cancellation_preserves_the_caller_token_instead_of_wrapped_comparer_failure()
    {
        using var cancellation = new CancellationTokenSource();
        var source = new CancelBeforeSort<SteamFriend>(
            [FriendQueryTests.Friend(1, "B", 1), FriendQueryTests.Friend(2, "A", 1)], cancellation);

        var exception = Assert.Throws<OperationCanceledException>(() =>
            FriendQuery.Apply(source, new(Sort: FriendSort.Name), cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.True(source.FullyEnumerated);
    }

    [Fact]
    public void Achievement_sort_cancellation_preserves_the_caller_token_instead_of_filter_failure()
    {
        using var cancellation = new CancellationTokenSource();
        var source = new CancelBeforeSort<AchievementDetail>(
            [new("b", "B", null, false, false, null), new("a", "A", null, false, false, null)], cancellation);

        var exception = Assert.Throws<OperationCanceledException>(() =>
            AchievementQuery.Apply(source, new(), cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.True(source.FullyEnumerated);
    }

    /// <summary>Deterministically cancels after filtering and before the first sort comparison.</summary>
    private sealed class CancelBeforeSort<T>(T[] values, CancellationTokenSource cancellation) : IReadOnlyList<T>
    {
        public int Count => values.Length;
        public T this[int index] => values[index];
        public bool FullyEnumerated { get; private set; }
        public IEnumerator<T> GetEnumerator()
        {
            foreach (var value in values) yield return value;
            FullyEnumerated = true;
            cancellation.Cancel();
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
