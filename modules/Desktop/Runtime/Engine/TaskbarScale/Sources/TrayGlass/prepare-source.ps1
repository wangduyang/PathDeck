#Requires -Version 7.0
param([string]$ArtifactRoot='D:\codex\TaskGlass\artifacts\tray-glass-20261126')
$ErrorActionPreference='Stop'
$upstream=Join-Path $ArtifactRoot 'upstream\windows-11-taskbar-styler.wh.cpp'
$script:source=[IO.File]::ReadAllText($upstream).Replace("`r`n","`n")
function Replace-Once([string]$Old,[string]$New){
    if(([regex]::Matches($script:source,[regex]::Escape($Old))).Count -ne 1){throw "Upstream safeguard anchor mismatch: $($Old.Substring(0,[Math]::Min(80,$Old.Length)))"}
    $script:source=$script:source.Replace($Old,$New)
}
# Keep the official style engine, WindhawkBlur and property restoration.
# Delete the restart command and prompt implementation entirely.
$start=$source.IndexOf('constexpr WCHAR kRestartExplorerPromptTitle[]')
$end=$source.IndexOf('// An InstanceHandle',$start)
if($start -lt 0 -or $end -lt $start){throw 'Restart prompt source anchor mismatch'}
$replacement=@'
// TaskGlass never requests or performs an Explorer restart.
void PromptToRestartExplorer(HRESULT statusCode) {
    Wh_Log(L"TaskGlass: XAML connection unavailable (%08X); Explorer is left running", statusCode);
}

'@
$script:source=$source.Substring(0,$start)+$replacement+"`n"+$source.Substring($end)
Replace-Once '    StartStatsTimer();' '    // TaskGlass: no theme statistics or background network timer.'
Replace-Once '    g_settings.xamlDiagnosticsHandling = XamlDiagnosticsHandling::kAlert;' '    g_settings.xamlDiagnosticsHandling = XamlDiagnosticsHandling::kAllow;'
Replace-Once @'
(_wcsicmp(lpClassName, L"XamlExplorerHostIslandWindow") == 0 ||
         _wcsicmp(lpClassName, L"Shell_InputSwitchTopLevelWindow") == 0)
'@ @'
(_wcsicmp(lpClassName, L"NotifyIconOverflowWindow") == 0 ||
         _wcsicmp(lpClassName, L"TopLevelWindowForOverflowXamlIsland") == 0)
'@
Replace-Once '            WCHAR szClassName[32];' '            WCHAR szClassName[64];'
Replace-Once @'
if (_wcsicmp(szClassName, L"XamlExplorerHostIslandWindow") == 0 ||
                _wcsicmp(szClassName, L"Shell_InputSwitchTopLevelWindow") ==
                    0)
'@ @'
if (_wcsicmp(szClassName, L"NotifyIconOverflowWindow") == 0 ||
                _wcsicmp(szClassName, L"TopLevelWindowForOverflowXamlIsland") == 0)
'@
# Some popup elements are constructed inside CreateWindow before the hook returns.
# Lazy initialization is restricted to actual overflow host threads (ZenDesktop approach).
Replace-Once 'thread_local bool g_initializedForThread;' @'
thread_local bool g_initializedForThread;

void InitializeForCurrentThread();
bool TaskGlassThreadOwnsOverflow() {
    bool found = false;
    EnumThreadWindows(GetCurrentThreadId(), [](HWND window, LPARAM value) -> BOOL {
        WCHAR name[64];
        if (GetClassNameW(window, name, ARRAYSIZE(name)) &&
            (_wcsicmp(name, L"NotifyIconOverflowWindow") == 0 ||
             _wcsicmp(name, L"TopLevelWindowForOverflowXamlIsland") == 0)) {
            *reinterpret_cast<bool*>(value) = true;
            return FALSE;
        }
        return TRUE;
    }, reinterpret_cast<LPARAM>(&found));
    return found;
}
'@
Replace-Once @'
    if (!g_initializedForThread)
    {
        Wh_Log(L"Not initialized for thread %u", GetCurrentThreadId());
        return S_OK;
    }
'@ @'
    if (!g_initializedForThread && g_initialized && TaskGlassThreadOwnsOverflow()) {
        InitializeForCurrentThread();
    }
    if (!g_initializedForThread)
    {
        Wh_Log(L"Not initialized for thread %u", GetCurrentThreadId());
        return S_OK;
    }
'@
$out=Join-Path $PSScriptRoot 'taskglass-tray-glass.wh.cpp'
[IO.File]::WriteAllText($out,$source,[Text.UTF8Encoding]::new($false))
[ordered]@{
    Repository='https://github.com/ramensoftware/windhawk-mods'
    Commit='6f3d126440ecd46f08e48bec2cd9c1e91d3e2919'
    Path='mods/windows-11-taskbar-styler.wh.cpp'
    Version='1.11.2';Author='m417z';License='GPL-3.0'
    SourceUrl='https://raw.githubusercontent.com/ramensoftware/windhawk-mods/6f3d126440ecd46f08e48bec2cd9c1e91d3e2919/mods/windows-11-taskbar-styler.wh.cpp'
    FetchedAt='2026-11-26'
    UpstreamSha256=(Get-FileHash -LiteralPath $upstream).Hash
    PatchedSha256=(Get-FileHash -LiteralPath $out).Hash
    Changes=@('Overflow host discovery and lazy initialization (ZenDesktop approach)','Remove restart-Explorer prompt implementation and command','Disable theme statistics startup','Allow other XAML diagnostics consumers by default')
    HostHandlingReference=[ordered]@{Repository='https://github.com/Liset999/ZenDesktop';Commit='fcd7fcd69e72d0c1ee0dbf87442f733b3db2a88f';Path='local@zen-taskbar-acrylic.wh.cpp';License='GPL-3.0'}
}|ConvertTo-Json -Depth 4|Set-Content -LiteralPath "$ArtifactRoot\upstream\Source.json" -Encoding utf8
Write-Output "Prepared overflow-scoped Taskbar Styler: $out"
