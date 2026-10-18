[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$ExecutablePath,[Parameter(Mandatory=$true)][string]$OutputPath)
$ErrorActionPreference='Stop'
Set-StrictMode -Version 2.0
. (Join-Path $PSScriptRoot 'ReleaseSupport.ps1')
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,WindowsBase
Add-Type -ReferencedAssemblies @([System.Windows.Automation.AutomationElement].Assembly.Location,[System.Windows.Automation.ControlType].Assembly.Location,[System.Windows.Rect].Assembly.Location) -TypeDefinition @'
using System;
using System.Threading;
using System.Runtime.InteropServices;
using System.Windows.Automation;
public static class GamePulseAccessibilityCheck {
    [DllImport("user32.dll")] static extern IntPtr GetWindowDpiAwarenessContext(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool AreDpiAwarenessContextsEqual(IntPtr a,IntPtr b);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
    static AutomationElement ById(AutomationElement root, string id) {
        var result=root.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.AutomationIdProperty,id));
        if(result==null) throw new InvalidOperationException("Missing accessible control: "+id);
        return result;
    }
    static void Check(bool condition,string message) { if(!condition) throw new InvalidOperationException(message); }
    static void Invoke(AutomationElement control) {
        object pattern;
        Check(control.TryGetCurrentPattern(InvokePattern.Pattern,out pattern),"Button must support InvokePattern.");
        ((InvokePattern)pattern).Invoke(); Thread.Sleep(120);
    }
    static void WaitName(AutomationElement root,string id,string expected) {
        var deadline=DateTime.UtcNow.AddSeconds(5);
        while(DateTime.UtcNow<deadline) {
            if(ById(root,id).Current.Name==expected) return;
            Thread.Sleep(50);
        }
        throw new InvalidOperationException("Accessible localized name did not update: "+id);
    }
    public static string Run(IntPtr handle, string expectedInvalidInput, string alternateInvalidInput, string expectedExplorer, string alternateExplorer) {
        var root=AutomationElement.FromHandle(handle);
        var initialStatus=ById(root,"ProfileStatus").Current.Name;
        var input=ById(root,"ProfileInput");
        Check(input.Current.ControlType==ControlType.Edit && input.Current.IsKeyboardFocusable,"Profile input must expose editable, focusable semantics.");
        Check(!String.IsNullOrWhiteSpace(input.Current.Name),"Profile input needs an accessible name.");
        input.SetFocus(); Thread.Sleep(120);
        Check(input.Equals(AutomationElement.FocusedElement),"UIA must focus profile input.");
        object value;
        Check(input.TryGetCurrentPattern(ValuePattern.Pattern,out value),"Profile input needs ValuePattern.");
        ((ValuePattern)value).SetValue("invalid display name with spaces");
        Invoke(ById(root,"SearchButton"));
        var deadline=DateTime.UtcNow.AddSeconds(5);
        var status=ById(root,"ProfileStatus").Current.Name;
        while(status!=expectedInvalidInput && DateTime.UtcNow<deadline) { Thread.Sleep(50); status=ById(root,"ProfileStatus").Current.Name; }
        Check(status!=initialStatus && status==expectedInvalidInput,"Invalid input must change the UIA status to the exact localized validation message.");
        Invoke(ById(root,"LanguageButton"));
        WaitName(root,"NavOverview",alternateExplorer);
        WaitName(root,"ProfileStatus",alternateInvalidInput);
        Check(((ValuePattern)value).Current.Value=="invalid display name with spaces","Changing language must retain the user's lookup input.");
        Invoke(ById(root,"LanguageButton"));
        WaitName(root,"NavOverview",expectedExplorer);
        WaitName(root,"ProfileStatus",expectedInvalidInput);
        string[] pages={"NavGames","NavFriends","NavAchievements","NavInventory","NavComparison","NavInsights","NavOverview"};
        foreach(var id in pages) { var button=ById(root,id); Check(!String.IsNullOrWhiteSpace(button.Current.Name),"Navigation needs accessible names."); Invoke(button); }
        Invoke(ById(root,"SettingsButton"));
        foreach(var id in new[]{"ThemeButton","SettingsButton","SearchButton"}) {
            var button=ById(root,id); object pattern;
            Check(button.TryGetCurrentPattern(InvokePattern.Pattern,out pattern),"Named action needs InvokePattern.");
        }
        var bounds=root.Current.BoundingRectangle;
        Check(AreDpiAwarenessContextsEqual(GetWindowDpiAwarenessContext(handle),new IntPtr(-4)),"Native window must be PerMonitorV2 aware.");
        return "{\"Passed\":true,\"NativeUiAutomation\":true,\"FocusableEdit\":true,\"ValuePattern\":true,\"InvalidInputStatus\":true,\"LiveLanguageNamesAndStatus\":true,\"LanguagePreservesInput\":true,\"NavigationInvokePatterns\":7,\"PerMonitorV2\":true,\"Dpi\":"+GetDpiForWindow(handle)+",\"WindowWidthPixels\":"+bounds.Width+",\"WindowHeightPixels\":"+bounds.Height+",\"NarratorSpokenReview\":false}";
    }
}
'@
$exe=(Resolve-Path -LiteralPath $ExecutablePath).Path
$previousKey=[Environment]::GetEnvironmentVariable('GAMEPULSE_STEAM_API_KEY','Process')
$child=$null
$previousDotEnv=[Environment]::GetEnvironmentVariable('GAMEPULSE_DISABLE_DOTENV','Process')

try {
    Set-GamePulseProcessSetting 'GAMEPULSE_DISABLE_DOTENV' '1'
    Set-GamePulseProcessSetting 'GAMEPULSE_STEAM_API_KEY' $null
    $child=Start-Process -FilePath $exe -WorkingDirectory (Split-Path -Parent $exe) -WindowStyle Hidden -PassThru
    $deadline=[DateTime]::UtcNow.AddSeconds(20)
    do { Start-Sleep -Milliseconds 150; $child.Refresh(); if ($child.HasExited) { throw 'App exited before native accessibility inspection.' } } while ($child.MainWindowHandle -eq 0 -and [DateTime]::UtcNow -lt $deadline)
    if ($child.MainWindowHandle -eq 0) { throw 'No native application window within deadline.' }
    Start-Sleep -Milliseconds 600
    $language=if ($env:GAMEPULSE_UI_LANGUAGE -ieq 'uk') { '.uk' } else { '' }
    [xml]$resources=Get-Content -LiteralPath (Join-Path $PSScriptRoot ('..\src\GamePulse.Desktop\Resources\Strings'+$language+'.resx')) -Raw -Encoding UTF8
    $expectedStatus=[string](@($resources.root.data | Where-Object { $_.name -eq 'InvalidInput' })[0].value)
    if (-not $expectedStatus) { throw 'The exact localized validation resource is missing.' }
    $alternateLanguage=if ($language -eq '.uk') { '' } else { '.uk' }
    [xml]$alternateResources=Get-Content -LiteralPath (Join-Path $PSScriptRoot ('..\src\GamePulse.Desktop\Resources\Strings'+$alternateLanguage+'.resx')) -Raw -Encoding UTF8
    $alternateStatus=[string](@($alternateResources.root.data | Where-Object { $_.name -eq 'InvalidInput' })[0].value)
    $expectedExplorer=[string](@($resources.root.data | Where-Object { $_.name -eq 'Explorer' })[0].value)
    $alternateExplorer=[string](@($alternateResources.root.data | Where-Object { $_.name -eq 'Explorer' })[0].value)
    $result=[GamePulseAccessibilityCheck]::Run($child.MainWindowHandle,$expectedStatus,$alternateStatus,$expectedExplorer,$alternateExplorer)
    $target=[IO.Path]::GetFullPath($OutputPath)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
    [IO.File]::WriteAllText($target,$result,(New-Object Text.UTF8Encoding($false)))
    Write-Host $result
}
finally {
    if ($child -and -not $child.HasExited) {
        $null=$child.CloseMainWindow()
        if (-not $child.WaitForExit(5000)) { $child.Kill() }
    }
    Set-GamePulseProcessSetting 'GAMEPULSE_DISABLE_DOTENV' $previousDotEnv
    Set-GamePulseProcessSetting 'GAMEPULSE_STEAM_API_KEY' $previousKey
}
