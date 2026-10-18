using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using GamePulse.Desktop.Controls;
using GamePulse.Desktop.Services;

namespace GamePulse.UiChecks;

/// <summary>Test-only images and delayed responses: no network or production demo data.</summary>
internal static class AsyncThumbnailChecks
{
    public static async Task RunAsync()
    {
        var loader = new DelayedImages();
        var thumbnail = new AsyncThumbnail { Width = 40, Height = 40, Loader = loader, Url = "row-a", FallbackText = "A" };
        Check(loader.Requests.Count == 0, "An unattached thumbnail must not load.");
        var parent = new StackPanel(); parent.Children.Add(thumbnail);
        var window = new Window { Width = 300, Height = 200, Content = parent };
        window.Show();
        try
        {
            await WaitUntilAsync(() => loader.Requests.Count == 1, window);
            var requestA = loader.Requests[0];
            // Simulate a recycled row while its old loader deliberately ignores cancellation.
            thumbnail.DataContext = new object();
            thumbnail.Url = "row-b";
            await WaitUntilAsync(() => loader.Requests.Any(request => request.Url == "row-b"), window);
            Check(requestA.Token.IsCancellationRequested, "Recycling a row must cancel its previous request.");
            var requestB = loader.Requests.Last(request => request.Url == "row-b");
            var bitmapB = Image(); requestB.Response.SetResult(bitmapB);
            await WaitUntilAsync(() => ReferenceEquals(thumbnail.CurrentImage, bitmapB), window);
            requestA.Response.SetResult(Image());
            await SettleAsync(window);
            Check(ReferenceEquals(thumbnail.CurrentImage, bitmapB), "A late old-row image must never replace the current image.");

            thumbnail.Url = "row-c";
            await WaitUntilAsync(() => loader.Requests.Any(request => request.Url == "row-c"), window);
            var requestC = loader.Requests.Last(request => request.Url == "row-c");
            parent.IsVisible = false;
            await SettleAsync(window);
            Check(requestC.Token.IsCancellationRequested && thumbnail.CurrentImage is null,
                "Hiding an ancestor must cancel media work and release the row bitmap.");
            var hiddenCalls = loader.Requests.Count;
            thumbnail.Url = "row-d";
            await SettleAsync(window);
            Check(loader.Requests.Count == hiddenCalls, "A hidden page must not begin thumbnail requests.");
            requestC.Response.SetResult(Image());
            await SettleAsync(window);
            Check(thumbnail.CurrentImage is null, "A hidden-page completion must be discarded.");
            parent.IsVisible = true;
            await WaitUntilAsync(() => loader.Requests.Any(request => request.Url == "row-d"), window);
            var requestD = loader.Requests.Last(request => request.Url == "row-d");
            parent.Children.Remove(thumbnail);
            Check(requestD.Token.IsCancellationRequested, "Detaching a recycled row must cancel its image request.");
            requestD.Response.SetResult(Image());
            await SettleAsync(window);
            Check(thumbnail.CurrentImage is null, "A detached control must reject a late completion.");
            // Resolve intermediate recycle requests too, proving they cannot restore a stale source.
            foreach (var request in loader.Requests.Where(request => !request.Response.Task.IsCompleted))
                request.Response.SetResult(Image());
            await SettleAsync(window);
            Check(thumbnail.CurrentImage is null, "Recycled intermediate responses must stay detached.");
        }
        finally
        {
            window.Close();
            foreach (var request in loader.Requests.Where(request => !request.Response.Task.IsCompleted))
                request.Response.SetResult(null);
        }
        Console.WriteLine("Visible-row thumbnail lifecycle passed: no detached/hidden work, cancellation, recycling and late-result rejection.");
    }

    private static Bitmap Image() => new WriteableBitmap(new PixelSize(4, 4), new Vector(96, 96));
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task WaitUntilAsync(Func<bool> complete, Window window)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!complete()) { deadline.Token.ThrowIfCancellationRequested(); await SettleAsync(window); }
    }
    private static async Task SettleAsync(Window window)
    {
        window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); await Task.Delay(30);
        window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
    }
    private sealed record Request(string Url, CancellationToken Token, TaskCompletionSource<Bitmap?> Response);
    private sealed class DelayedImages : IThumbnailLoader
    {
        public List<Request> Requests { get; } = [];
        public Task<Bitmap?> LoadAsync(string? url, CancellationToken token)
        {
            var response = new TaskCompletionSource<Bitmap?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Requests.Add(new(url!, token, response));
            return response.Task;
        }
    }
}
