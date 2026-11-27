#Requires -Version 7.0
param(
    [string]$VendorRoot = 'D:\codex\.tmp\taskglass-start-v2-20261123\vendor',
    [string]$CompilerRoot = 'D:\codex\.tmp\taskglass-start-engine-20261123\vendor\Compiler',
    [string]$ArtifactRoot = 'D:\codex\TaskGlass\artifacts\start-menu-pathdeck-v2-20261123'
)
$ErrorActionPreference='Stop'
$runtime=Join-Path $ArtifactRoot 'engine'
$mods=Join-Path $runtime 'Data\Mods\64'
$logs=Join-Path $PSScriptRoot 'build-v2'
New-Item -ItemType Directory -Path $runtime,$mods,$logs -Force | Out-Null
foreach($architecture in '32','64') {
    $source=Join-Path $VendorRoot "Engine\2.0.0\$architecture"
    $destination=Join-Path $runtime $architecture
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    foreach($file in 'windhawk.dll','msdia140_windhawk.dll','symsrv_windhawk.dll','symsrv.yes') {Copy-Item -LiteralPath "$source\$file" -Destination $destination -Force}
}
[IO.File]::WriteAllText("$runtime\engine.ini","[Storage]`r`nPortable=1`r`nAppDataPath=Data`r`n",[Text.Encoding]::Unicode)
$start=[Diagnostics.ProcessStartInfo]::new((Join-Path $CompilerRoot 'bin\clang++.exe'))
$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.WorkingDirectory=$CompilerRoot
$start.RedirectStandardInput=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
$arguments=@('-std=c++23','-O2','-static','-municode','-mwindows','-DUNICODE','-D_UNICODE','-D_WIN32_WINNT=0x0A00','-target','x86_64-w64-mingw32','-x','c++','-','-lshell32','-o',"$runtime\TaskGlass.StartHost.exe")
foreach($argument in $arguments){$start.ArgumentList.Add($argument)}
$compiler=[Diagnostics.Process]::Start($start)
$output=$compiler.StandardOutput.ReadToEndAsync();$errors=$compiler.StandardError.ReadToEndAsync()
$compiler.StandardInput.Write([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'StartHost.cpp')))
$compiler.StandardInput.Close();$compiler.WaitForExit()
[IO.File]::WriteAllText("$logs\helper.stdout.txt",$output.GetAwaiter().GetResult())
[IO.File]::WriteAllText("$logs\helper.stderr.txt",$errors.GetAwaiter().GetResult())
if($compiler.ExitCode -ne 0){throw "Host build failed: $($compiler.ExitCode)"}
$compiler.Dispose()
Copy-Item -LiteralPath 'D:\codex\TaskGlass\artifacts\start-menu-pathdeck-20261123\engine\Data\Mods\64\TaskGlass.StartMenuStyler.dll' -Destination $mods -Force
foreach($file in 'libc++.whl','libunwind.whl','windhawk-mod-shim.dll'){Copy-Item -LiteralPath "$VendorRoot\ModsRuntime\64\$file" -Destination $mods -Force}
$arguments | ConvertTo-Json | Set-Content -LiteralPath "$logs\command.json" -Encoding utf8
$probe=Start-Process -FilePath "$runtime\TaskGlass.StartHost.exe" -ArgumentList '--probe' -WindowStyle Hidden -PassThru -Wait
if($probe.ExitCode -ne 0){throw "Native x64 engine probe failed: $($probe.ExitCode)"}
Write-Output 'Built native x64 API host; official engine exports probe passed.'
