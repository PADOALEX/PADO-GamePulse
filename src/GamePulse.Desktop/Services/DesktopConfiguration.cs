using GamePulse.Infrastructure;

namespace GamePulse.Desktop.Services;

/// <summary>Startup-only desktop configuration. Never mutates the process environment or exposes values in diagnostics.</summary>
public sealed class DesktopConfiguration
{
    public SteamOptions Steam { get; private init; } = new();
    public string? UiLanguage { get; private init; }
    public override string ToString() => nameof(DesktopConfiguration);

    public static DesktopConfiguration Load() => Load(Environment.GetEnvironmentVariable,
        Environment.CurrentDirectory, AppContext.BaseDirectory);

    public static DesktopConfiguration Load(Func<string, string?> environment, string workingDirectory, string executableDirectory)
    {
        var key = environment("GAMEPULSE_STEAM_API_KEY");
        var language = environment("GAMEPULSE_UI_LANGUAGE");
        var disabled = environment("GAMEPULSE_DISABLE_DOTENV");
        // Even an explicitly empty environment value wins over a file. Tests and CI can
        // disable all local file reads, independently of whether a developer has a key.
        if (disabled == "1" || string.Equals(disabled, "true", StringComparison.OrdinalIgnoreCase)
            || (key is not null && language is not null))
            return new() { Steam = new() { ApiKey = key }, UiLanguage = language };

        var local = LocalEnvironmentFile.ReadAllowedSettings(workingDirectory);
        var adjacent = SameDirectory(workingDirectory, executableDirectory)
            ? local : LocalEnvironmentFile.ReadAllowedSettings(executableDirectory);
        return new()
        {
            Steam = new() { ApiKey = key ?? local.ApiKey ?? adjacent.ApiKey },
            UiLanguage = language ?? local.UiLanguage ?? adjacent.UiLanguage
        };
    }

    private static bool SameDirectory(string first, string second)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)).Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException)
        { return false; }
    }
}
