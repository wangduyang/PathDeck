#Requires -Version 7.0
param(
    [string]$CompilerRoot='D:\codex\.tmp\taskglass-start-engine-20261123\vendor\Compiler',
    [string]$VendorRoot='D:\codex\.tmp\taskglass-start-v2-20261123\vendor',
    [string]$ArtifactRoot='D:\codex\TaskGlass\artifacts\tray-glass-20261126'
)
$ErrorActionPreference='Stop'
& "$PSScriptRoot\prepare-source.ps1" -ArtifactRoot $ArtifactRoot
$runtime=Join-Path $ArtifactRoot 'component'
$mods=Join-Path $runtime 'Data\Mods\64'
$sources=Join-Path $runtime 'Sources\TrayGlass'
$logs=Join-Path $ArtifactRoot 'build'
New-Item -ItemType Directory -Path $mods,$sources,$logs -Force|Out-Null
$modSource=Join-Path $PSScriptRoot 'taskglass-tray-glass.wh.cpp'
$dll=Join-Path $mods 'TaskGlass.TrayGlass.dll'
$arguments=@('-std=c++23','-O2','-shared','-DUNICODE','-D_UNICODE','-DWINVER=0x0A00','-D_WIN32_WINNT=0x0A00','-D_WIN32_IE=0x0A00','-DNTDDI_VERSION=0x0A000008','-D__USE_MINGW_ANSI_STDIO=0','-DWH_MOD','-DWH_MOD_ID=L"taskglass-tray-glass"','-DWH_MOD_VERSION=L"1.11.2"',"$VendorRoot\Engine\2.0.0\64\windhawk.lib",'-x','c++','-','-include','windhawk_api.h','-target','x86_64-w64-mingw32','-Wl,--export-all-symbols','-o',$dll,'-lcomctl32','-lgdi32','-lole32','-loleaut32','-lruntimeobject','-lshlwapi')
$start=[Diagnostics.ProcessStartInfo]::new("$CompilerRoot\bin\clang++.exe")
$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.WorkingDirectory=$CompilerRoot
$start.RedirectStandardInput=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
foreach($argument in $arguments){$start.ArgumentList.Add($argument)}
$process=[Diagnostics.Process]::Start($start)
$stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
$process.StandardInput.Write([IO.File]::ReadAllText($modSource));$process.StandardInput.Close();$process.WaitForExit()
[IO.File]::WriteAllText("$logs\mod.stdout.txt",$stdout.GetAwaiter().GetResult())
[IO.File]::WriteAllText("$logs\mod.stderr.txt",$stderr.GetAwaiter().GetResult())
$code=$process.ExitCode;$process.Dispose()
if($code -ne 0){throw "Tray glass build failed ($code); see $logs"}
Copy-Item -LiteralPath $modSource,"$PSScriptRoot\prepare-source.ps1","$PSScriptRoot\build.ps1","$PSScriptRoot\verify-component.ps1","$PSScriptRoot\README.md" -Destination $sources -Force
Copy-Item -LiteralPath "$ArtifactRoot\upstream\windows-11-taskbar-styler.wh.cpp","$ArtifactRoot\upstream\Source.json" -Destination $sources -Force
Copy-Item -LiteralPath 'D:\codex\TaskGlass\artifacts\zendesktop-reference-20261126\LICENSE' -Destination "$sources\LICENSE-GPL-3.0" -Force
$manifest=Get-Content -LiteralPath "$ArtifactRoot\upstream\Source.json" -Raw|ConvertFrom-Json -AsHashtable
$manifest.DllSha256=(Get-FileHash -LiteralPath $dll).Hash
$manifest.Runtime='Existing Engine/TaskbarScale Windhawk 2.0 alpha8 Explorer-only session; no additional host'
$manifest.ModuleId='taskglass-tray-glass'
$manifest.Profile='theme empty; only NotificationAreaOverflow control/root transparent + background brush/corners; original icons/input retained'
$manifest|ConvertTo-Json -Depth 4|Set-Content -LiteralPath "$sources\third-party-manifest.json" -Encoding utf8
$arguments|ConvertTo-Json|Set-Content -LiteralPath "$logs\command.json" -Encoding utf8
Write-Output "Built native tray glass component: $runtime"
