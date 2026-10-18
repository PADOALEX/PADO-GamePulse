using GamePulse.Desktop.Services;
using GamePulse.Infrastructure;
using Xunit;

namespace GamePulse.UnitTests;

public sealed class DesktopConfigurationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "GamePulse-config-test-" + Guid.NewGuid().ToString("N"));
    private string Working => Path.Combine(root, "working");
    private string Executable => Path.Combine(root, "executable");
    public DesktopConfigurationTests()
    { Directory.CreateDirectory(Working); Directory.CreateDirectory(Executable); }
    private static string? None(string _) => null;

    [Fact]
    public void Working_file_overrides_adjacent_file_per_allowed_setting_without_mutating_environment()
    {
        File.WriteAllText(Path.Combine(Working, ".env"), "GAMEPULSE_STEAM_API_KEY=working-test-only\n");
        File.WriteAllText(Path.Combine(Executable, ".env"), "GAMEPULSE_STEAM_API_KEY=adjacent-test-only\nGAMEPULSE_UI_LANGUAGE=uk\n");
        var result = DesktopConfiguration.Load(None, Working, Executable);
        Assert.Equal("working-test-only", result.Steam.ApiKey);
        Assert.Equal("uk", result.UiLanguage);
        Assert.DoesNotContain("working-test-only", result.ToString());
        Assert.DoesNotContain("working-test-only", result.Steam.ToString());
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("environment-test-only", "en")]
    public void Explicit_environment_values_including_empty_win_over_local_files(string key, string language)
    {
        File.WriteAllText(Path.Combine(Working, ".env"), "GAMEPULSE_STEAM_API_KEY=file-test-only\nGAMEPULSE_UI_LANGUAGE=uk\n");
        string? EnvironmentValue(string name) => name switch
        { "GAMEPULSE_STEAM_API_KEY" => key, "GAMEPULSE_UI_LANGUAGE" => language, _ => null };
        var result = DesktopConfiguration.Load(EnvironmentValue, Working, Executable);
        Assert.Equal(key, result.Steam.ApiKey);
        Assert.Equal(language, result.UiLanguage);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("TRUE")]
    public void Automation_opt_out_never_uses_local_settings(string disabled)
    {
        File.WriteAllText(Path.Combine(Working, ".env"), "GAMEPULSE_STEAM_API_KEY=file-test-only\nGAMEPULSE_UI_LANGUAGE=uk\n");
        var result = DesktopConfiguration.Load(name => name == "GAMEPULSE_DISABLE_DOTENV" ? disabled : null, Working, Executable);
        Assert.False(result.Steam.HasApiKey);
        Assert.Null(result.UiLanguage);
    }

    [Fact]
    public void Absent_files_and_parent_file_do_not_supply_a_key()
    {
        File.WriteAllText(Path.Combine(root, ".env"), "GAMEPULSE_STEAM_API_KEY=parent-must-not-load\n");
        Assert.False(DesktopConfiguration.Load(None, Working, Executable).Steam.HasApiKey);
    }

    [Fact]
    public void One_directory_is_supported_and_unknown_legacy_key_names_are_not_used()
    {
        File.WriteAllText(Path.Combine(Working, ".env"), "API_KEY=old-unsupported-test-only\napi_key=wrong-case\nGAMEPULSE_UI_LANGUAGE=uk\n");
        var result = DesktopConfiguration.Load(None, Working, Working);
        Assert.False(result.Steam.HasApiKey);
        Assert.Equal("uk", result.UiLanguage);
    }

    [Theory]
    [InlineData("GAMEPULSE_STEAM_API_KEY=test-key\n", "test-key")]
    [InlineData("\uFEFFGAMEPULSE_STEAM_API_KEY=\"test-key\"\r\n", "test-key")]
    [InlineData("# comment\nexport GAMEPULSE_STEAM_API_KEY = 'test-key' # comment\n", "test-key")]
    [InlineData("GAMEPULSE_STEAM_API_KEY=test-key # comment\n", "test-key")]
    [InlineData("GAMEPULSE_STEAM_API_KEY=with=equals\n", "with=equals")]
    [InlineData("GAMEPULSE_STEAM_API_KEY='with#hash'\n", "with#hash")]
    [InlineData("GAMEPULSE_STEAM_API_KEY=literal#hash\n", "literal#hash")]
    [InlineData("GAMEPULSE_STEAM_API_KEY=${NOT_EXPANDED}\n", "${NOT_EXPANDED}")]
    [InlineData("GAMEPULSE_STEAM_API_KEY='$(not executed)'\n", "$(not executed)")]
    public void Local_parser_accepts_supported_literal_syntax_without_execution(string contents, string expected)
    {
        File.WriteAllText(Path.Combine(Working, ".env"), contents, new System.Text.UTF8Encoding(false));
        Assert.Equal(expected, LocalEnvironmentFile.ReadAllowedSettings(Working).ApiKey);
    }

    [Theory]
    [InlineData("GAMEPULSE_STEAM_API_KEY='unterminated")]
    [InlineData("GAMEPULSE_STEAM_API_KEY=\"key\"junk")]
    [InlineData("GAMEPULSE_STEAM_API_KEY key")]
    [InlineData("GAMEPULSE_STEAM_API_KEY=has\"quote")]
    [InlineData("GAMEPULSE_STEAM_API_KEY='has\0control'")]
    public void Malformed_supported_assignments_reject_the_file_without_exposing_partial_values(string malformed)
    {
        File.WriteAllText(Path.Combine(Working, ".env"), "GAMEPULSE_UI_LANGUAGE=uk\n" + malformed);
        var result = LocalEnvironmentFile.ReadAllowedSettings(Working);
        Assert.Null(result.ApiKey);
        Assert.Null(result.UiLanguage);
        Assert.Equal(nameof(LocalEnvironmentSettings), result.ToString());
    }

    [Fact]
    public void Oversized_invalid_utf8_and_directory_instead_of_file_are_gracefully_ignored()
    {
        var path = Path.Combine(Working, ".env");
        File.WriteAllText(path, "GAMEPULSE_STEAM_API_KEY=" + new string('a', LocalEnvironmentFile.MaximumFileBytes));
        Assert.Null(LocalEnvironmentFile.ReadAllowedSettings(Working).ApiKey);
        File.WriteAllBytes(path, [0xff, 0xff, 0xff]);
        Assert.Null(LocalEnvironmentFile.ReadAllowedSettings(Working).ApiKey);
        File.Delete(path);
        Directory.CreateDirectory(path);
        Assert.Null(LocalEnvironmentFile.ReadAllowedSettings(Working).ApiKey);
    }

    public void Dispose()
    {
        // Only this fresh, fixed-prefix test directory can be removed.
        if (Path.GetFileName(root).StartsWith("GamePulse-config-test-", StringComparison.Ordinal)
            && Path.GetDirectoryName(root) == Path.TrimEndingDirectorySeparator(Path.GetTempPath()))
            Directory.Delete(root, true);
    }
}
