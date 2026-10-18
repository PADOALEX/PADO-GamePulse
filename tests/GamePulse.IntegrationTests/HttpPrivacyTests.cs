using System.Diagnostics;
using System.Runtime.CompilerServices;
using GamePulse.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GamePulse.IntegrationTests;

internal static class TestHttpStartup
{
    // Match Program.Main: the runtime caches this HTTP setting, so configure before the first client.
    [ModuleInitializer]
    internal static void ConfigurePrivacy() => SteamHttpPrivacy.ConfigureBeforeHttpClients();
}

public sealed class HttpPrivacyTests
{
    [Fact]
    public async Task Real_socket_handler_diagnostics_do_not_expose_query_key()
    {
        const string sentinel = "test-diagnostic-key-sentinel";
        using var observer = new DiagnosticObserver(sentinel);
        using var allListeners = DiagnosticListener.AllListeners.Subscribe(observer);
        using var handler = new SocketsHttpHandler
        {
            // Exercise the real diagnostics pipeline without making a network connection.
            ConnectCallback = (_, _) => ValueTask.FromException<Stream>(new IOException("No network in this test."))
        };
        using var client = new SteamApiClient(new HttpClient(handler),
            new SteamOptions { ApiKey = sentinel, MaxAttempts = 1 }, NullLogger<SteamApiClient>.Instance,
            TimeProvider.System, new RetryDelay(TimeProvider.System), new());
        var result = await client.GetProfileAsync("76561198000000001", default);
        Assert.False(result.IsReady);
        Assert.False(observer.SecretObserved);
        Assert.DoesNotContain(sentinel, result.Message!);
        Assert.True(AppContext.TryGetSwitch("System.Net.Http.EnableActivityPropagation", out var enabled));
        Assert.False(enabled);
    }

    private sealed class DiagnosticObserver(string sentinel) : IObserver<DiagnosticListener>,
        IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly List<IDisposable> subscriptions = [];
        public bool SecretObserved { get; private set; }
        public void OnNext(DiagnosticListener value)
        {
            if (value.Name == "HttpHandlerDiagnosticListener") subscriptions.Add(value.Subscribe(this));
        }
        public void OnNext(KeyValuePair<string, object?> value)
        {
            // Store only a Boolean: an assertion failure must never print the sensitive URI.
            var request = value.Value?.GetType().GetProperty("Request")?.GetValue(value.Value) as HttpRequestMessage;
            if (request?.RequestUri?.AbsoluteUri.Contains(sentinel, StringComparison.Ordinal) == true)
                SecretObserved = true;
        }
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void Dispose()
        {
            foreach (var subscription in subscriptions) subscription.Dispose();
        }
    }
}
