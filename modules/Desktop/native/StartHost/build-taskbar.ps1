#Requires -Version 7.0
param(
    [string]$CompilerRoot='D:\codex\.tmp\taskglass-start-engine-20261123\vendor\Compiler',
    [string]$VendorRoot='D:\codex\.tmp\taskglass-start-v2-20261123\vendor',
    [string]$ArtifactRoot='D:\codex\TaskGlass\artifacts\taskbar-scale-20261126',
    [string]$ModSource='D:\codex\TaskGlass\artifacts\zendesktop-reference-20261126\local@zen-taskbar-height-and-icon-size.wh.cpp'
)
$ErrorActionPreference='Stop'
$runtime=Join-Path $ArtifactRoot 'engine'
$mods=Join-Path $runtime 'Data\Mods\64'
$sources=Join-Path $runtime 'Sources'
$logs=Join-Path $ArtifactRoot 'build'
New-Item -ItemType Directory -Path $mods,$sources,$logs -Force|Out-Null
foreach($architecture in '32','64'){
    $destination=Join-Path $runtime $architecture
    New-Item -ItemType Directory -Path $destination -Force|Out-Null
    foreach($file in 'windhawk.dll','msdia140_windhawk.dll','symsrv_windhawk.dll','symsrv.yes'){
        Copy-Item -LiteralPath "$VendorRoot\Engine\2.0.0\$architecture\$file" -Destination $destination -Force
    }
}
[IO.File]::WriteAllText("$runtime\engine.ini","[Storage]`r`nPortable=1`r`nAppDataPath=Data`r`n",[Text.Encoding]::Unicode)
function Invoke-Compiler([string[]]$Arguments,[string]$Source,[string]$Name){
    $start=[Diagnostics.ProcessStartInfo]::new("$CompilerRoot\bin\clang++.exe")
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.WorkingDirectory=$CompilerRoot
    $start.RedirectStandardInput=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    foreach($argument in $Arguments){$start.ArgumentList.Add($argument)}
    $process=[Diagnostics.Process]::Start($start)
    $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
    $process.StandardInput.Write([IO.File]::ReadAllText($Source));$process.StandardInput.Close();$process.WaitForExit()
    [IO.File]::WriteAllText("$logs\$Name.stdout.txt",$stdout.GetAwaiter().GetResult())
    [IO.File]::WriteAllText("$logs\$Name.stderr.txt",$stderr.GetAwaiter().GetResult())
    $code=$process.ExitCode;$process.Dispose()
    if($code -ne 0){throw "$Name build failed ($code); see $logs"}
}
$hostArgs=@('-std=c++23','-O2','-static','-municode','-mwindows','-DTASKGLASS_TASKBAR_SCALE','-DUNICODE','-D_UNICODE','-D_WIN32_WINNT=0x0A00','-target','x86_64-w64-mingw32','-x','c++','-','-lshell32','-o',"$runtime\TaskGlass.TaskbarHost.exe")
$modArgs=@('-std=c++23','-O2','-shared','-DUNICODE','-D_UNICODE','-DWINVER=0x0A00','-D_WIN32_WINNT=0x0A00','-D_WIN32_IE=0x0A00','-DNTDDI_VERSION=0x0A000008','-D__USE_MINGW_ANSI_STDIO=0','-DWH_MOD','-DWH_MOD_ID=L"taskglass-taskbar-scale"','-DWH_MOD_VERSION=L"1.3.11"',"$VendorRoot\Engine\2.0.0\64\windhawk.lib",'-x','c++','-','-include','windhawk_api.h','-target','x86_64-w64-mingw32','-Wl,--export-all-symbols','-o',"$mods\TaskGlass.TaskbarScale.dll",'-lole32','-loleaut32','-lruntimeobject','-lshcore','-lversion')
Invoke-Compiler $hostArgs "$PSScriptRoot\StartHost.cpp" 'host'
Invoke-Compiler $modArgs $ModSource 'mod'
foreach($file in 'libc++.whl','libunwind.whl','windhawk-mod-shim.dll'){
    Copy-Item -LiteralPath "D:\codex\TaskGlass\run\Engine\StartMenuV2\Data\Mods\64\$file" -Destination $mods -Force
}
Copy-Item -LiteralPath $ModSource -Destination "$sources\taskbar-height-and-icon-size.wh.cpp" -Force
Copy-Item -LiteralPath "$PSScriptRoot\StartHost.cpp","$PSScriptRoot\build-taskbar.ps1" -Destination $sources -Force
Copy-Item -LiteralPath 'D:\codex\TaskGlass\artifacts\zendesktop-reference-20261126\LICENSE' -Destination "$runtime\LICENSE-GPL-3.0" -Force
[ordered]@{Repository='https://github.com/Liset999/ZenDesktop';Commit='fcd7fcd69e72d0c1ee0dbf87442f733b3db2a88f';Path='local@zen-taskbar-height-and-icon-size.wh.cpp';Version='1.3.11';Author='m417z and Lanbo';License='GPL-3.0';SourceSha256=(Get-FileHash -LiteralPath $ModSource).Hash;DllSha256=(Get-FileHash -LiteralPath "$mods\TaskGlass.TaskbarScale.dll").Hash;HostSha256=(Get-FileHash -LiteralPath "$runtime\TaskGlass.TaskbarHost.exe").Hash}|ConvertTo-Json|Set-Content -LiteralPath "$runtime\third-party-manifest.json" -Encoding utf8
[ordered]@{Host=$hostArgs;Mod=$modArgs}|ConvertTo-Json -Depth 3|Set-Content -LiteralPath "$logs\commands.json" -Encoding utf8
$probe=Start-Process -FilePath "$runtime\TaskGlass.TaskbarHost.exe" -ArgumentList '--probe' -WindowStyle Hidden -PassThru -Wait
if($probe.ExitCode -ne 0){throw "Taskbar host export probe failed ($($probe.ExitCode))"}
Write-Output "Built taskbar-only host and community size module; engine export probe passed. Runtime: $runtime"
