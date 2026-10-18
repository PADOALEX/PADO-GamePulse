using GamePulse.Core;
using Xunit;

namespace GamePulse.UnitTests;

public class SearchSessionTests
{
    [Fact]
    public async Task OldCompletionCannotReplaceNewSearchEvenWhenTransportIgnoresCancellation()
    {
        using var session = new SearchSession();
        var pendingA = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingB = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = session.Begin();
        var second = session.Begin();
        Assert.True(first.Token.IsCancellationRequested);
        Assert.False(second.Token.IsCancellationRequested);

        pendingB.SetResult("Profile B");
        string? visible = null;
        var b = await pendingB.Task;
        if (session.IsCurrent(second.Generation)) visible = b;
        pendingA.SetResult("Profile A");
        var a = await pendingA.Task;
        if (session.IsCurrent(first.Generation)) visible = a;
        Assert.Equal("Profile B", visible);
    }

    [Fact]
    public void CancelAndDisposeInvalidateAllPriorGenerations()
    {
        var session = new SearchSession();
        var first = session.Begin();
        session.Cancel();
        Assert.True(first.Token.IsCancellationRequested);
        Assert.False(session.IsCurrent(first.Generation));
        var second = session.Begin();
        session.Dispose();
        Assert.True(second.Token.IsCancellationRequested);
        Assert.False(session.IsCurrent(second.Generation));
    }

    [Fact]
    public void MissingBanValuesStayUnknown()
    {
        var unknown = new SteamBans(null, null, null, null, null, null);
        Assert.Null(unknown.VacBanned);
        Assert.Null(unknown.VacBanCount);
        Assert.Null(Outcome<SteamBans>.Failure(DataState.Unavailable, "Unavailable").Data);
    }
}
