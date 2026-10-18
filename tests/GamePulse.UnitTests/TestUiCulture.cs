using System.Globalization;
using System.Runtime.CompilerServices;

namespace GamePulse.UnitTests;

internal static class TestUiCulture
{
    // View-model assertions use the product's default English UI on every OS locale.
    // Numeric/date formatting deliberately retains the host culture, as production does.
    [ModuleInitializer]
    internal static void Initialize()
    {
        var english = CultureInfo.GetCultureInfo("en");
        CultureInfo.DefaultThreadCurrentUICulture = english;
        CultureInfo.CurrentUICulture = english;
    }
}
