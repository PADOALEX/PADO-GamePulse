namespace GamePulse.Infrastructure;

public static class SteamHttpPrivacy
{
    /// <summary>
    /// Call at process startup, before constructing any HTTP clients. Steam requires a query-key:
    /// removing ILogger handlers alone does not prevent DiagnosticListener from exposing RequestUri.
    /// This process-wide switch also disables HTTP distributed-activity propagation.
    /// </summary>
    public static void ConfigureBeforeHttpClients() =>
        AppContext.SetSwitch("System.Net.Http.EnableActivityPropagation", false);
}
