#Requires -Version 7.0
param([string]$ArtifactRoot='D:\codex\TaskGlass\artifacts\tray-glass-20261126')
$ErrorActionPreference='Stop'
$runtime=Join-Path $ArtifactRoot 'component'
$dll=Join-Path $runtime 'Data\Mods\64\TaskGlass.TrayGlass.dll'
$sources=Join-Path $runtime 'Sources\TrayGlass'
$manifest=Get-Content -LiteralPath "$sources\third-party-manifest.json" -Raw|ConvertFrom-Json
if((Get-FileHash -LiteralPath $dll).Hash -ne $manifest.DllSha256){throw 'Native DLL manifest hash mismatch'}
if((Get-FileHash -LiteralPath "$sources\taskglass-tray-glass.wh.cpp").Hash -ne $manifest.PatchedSha256){throw 'Patched source manifest hash mismatch'}
if((Get-FileHash -LiteralPath "$sources\windows-11-taskbar-styler.wh.cpp").Hash -ne $manifest.UpstreamSha256){throw 'Upstream source manifest hash mismatch'}
$source=[IO.File]::ReadAllText("$sources\taskglass-tray-glass.wh.cpp")
if($source.Contains('kRestartExplorerCommand') -or $source.Contains('taskkill /f /im explorer.exe')){throw 'Explorer restart implementation must be removed'}
if($source.Contains('    StartStatsTimer();')){throw 'Theme statistics must not start'}
foreach($target in 'NotifyIconOverflowWindow','TopLevelWindowForOverflowXamlIsland','TaskGlassThreadOwnsOverflow'){
    if(-not $source.Contains($target)){throw "Missing overflow lifecycle safeguard: $target"}
}
foreach($file in 'Data\settings.ini','engine.ini','TaskGlass.TaskbarHost.exe','Data\Mods\taskglass-taskbar-scale.ini'){
    if(Test-Path -LiteralPath (Join-Path $runtime $file)){throw "Component must not overwrite existing Scale session: $file"}
}
if((Get-Item -LiteralPath "$ArtifactRoot\build\mod.stderr.txt").Length -ne 0){throw 'Native build diagnostics must be reviewed'}
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class TrayGlassExportProbe {
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    public static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);
    [DllImport("kernel32.dll",CharSet=CharSet.Ansi,ExactSpelling=true)]
    public static extern IntPtr GetProcAddress(IntPtr module,string name);
    [DllImport("kernel32.dll")]
    public static extern bool FreeLibrary(IntPtr module);
}
'@
# DONT_RESOLVE_DLL_REFERENCES maps the PE without executing DllMain or Wh_ModInit.
$module=[TrayGlassExportProbe]::LoadLibraryExW($dll,[IntPtr]::Zero,1)
if($module -eq [IntPtr]::Zero){throw "Native PE mapping failed: $([Runtime.InteropServices.Marshal]::GetLastWin32Error())"}
try{
    # The official Clang/GNU target exports these C++ ABI names (as does Scale).
    foreach($name in '_Z10Wh_ModInitv','_Z15Wh_ModAfterInitv','_Z12Wh_ModUninitv','_Z21Wh_ModSettingsChangedv'){
        if([TrayGlassExportProbe]::GetProcAddress($module,$name) -eq [IntPtr]::Zero){throw "Missing Windhawk export: $name"}
    }
}finally{[void][TrayGlassExportProbe]::FreeLibrary($module)}
[ordered]@{VerifiedAt=(Get-Date).ToString('o');DllSha256=$manifest.DllSha256;NativeBuild='Clang x64 succeeded; zero compiler diagnostics';Exports='All four Windhawk lifecycle exports found without initialization';Profile='Managed synthetic configuration checks run separately';Safeguards='No Explorer restart implementation; no statistics startup; native overflow lifecycle; existing host/global/scale files untouched';RuntimeInteraction='None; native popup appearance and restoration require root live verification'}|ConvertTo-Json|Set-Content -LiteralPath "$ArtifactRoot\build\verification.json" -Encoding utf8
Write-Output 'PASS: native x64 DLL lifecycle exports, source/DLL provenance hashes, GPL source package, no restart/statistics startup, additive component scope. No module initialization or desktop input.'
