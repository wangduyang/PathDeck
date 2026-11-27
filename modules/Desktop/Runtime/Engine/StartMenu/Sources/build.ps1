#Requires -Version 7.0
param(
    [string]$VendorRoot = 'D:\codex\.tmp\taskglass-start-engine-20261123\vendor',
    [string]$ArtifactRoot = 'D:\codex\TaskGlass\artifacts\start-menu-pathdeck-20261123'
)
$ErrorActionPreference = 'Stop'
$compilerRoot = Join-Path $VendorRoot 'Compiler'
$clang = Join-Path $compilerRoot 'bin\clang++.exe'
$runtime = Join-Path $ArtifactRoot 'engine'
$logs = Join-Path $PSScriptRoot 'build'
$mods = Join-Path $runtime 'Data\Mods\64'
New-Item -ItemType Directory -Force -Path $runtime, $logs, $mods | Out-Null
foreach ($architecture in @('32', '64')) {
    $engineArchitecture = Join-Path $VendorRoot ('Engine\1.7.3\' + $architecture)
    $destination = Join-Path $runtime $architecture
    New-Item -ItemType Directory -Force -Path $destination | Out-Null
    foreach ($file in @('windhawk.dll', 'msdia140_windhawk.dll', 'symsrv_windhawk.dll', 'symsrv.yes')) {
        Copy-Item -LiteralPath (Join-Path $engineArchitecture $file) -Destination $destination -Force
    }
}
[IO.File]::WriteAllText((Join-Path $runtime 'engine.ini'), "[Storage]`r`nPortable=1`r`nAppDataPath=Data`r`n", [Text.Encoding]::Unicode)
$helperArgs = @('-std=c++23', '-O2', '-static', '-municode', '-mwindows',
    '-DUNICODE', '-D_UNICODE', '-D_WIN32_WINNT=0x0A00', '-target', 'i686-w64-mingw32',
    '-x', 'c++', '-', '-lshell32',
    '-o', (Join-Path $runtime 'TaskGlass.StartHost.exe'))
$modArgs = @('-std=c++23', '-O2', '-shared', '-DUNICODE', '-D_UNICODE',
    '-DWINVER=0x0A00', '-D_WIN32_WINNT=0x0A00', '-D_WIN32_IE=0x0A00',
    '-DNTDDI_VERSION=0x0A000008', '-D__USE_MINGW_ANSI_STDIO=0', '-DWH_MOD',
    '-DWH_MOD_ID=L"windows-11-start-menu-styler"', '-DWH_MOD_VERSION=L"1.8"',
    (Join-Path $VendorRoot 'Engine\1.7.3\64\windhawk.lib'),
    '-x', 'c++', '-',
    '-include', 'windhawk_api.h', '-target', 'x86_64-w64-mingw32',
    '-Wl,--export-all-symbols', '-o', (Join-Path $mods 'TaskGlass.StartMenuStyler.dll'),
    '-lcomctl32', '-lole32', '-loleaut32', '-lruntimeobject', '-lshlwapi', '-lversion')
function Invoke-Compiler([string[]]$CompilerArguments, [string]$SourcePath, [string]$LogName) {
    $startInfo = [Diagnostics.ProcessStartInfo]::new($clang)
    $startInfo.WorkingDirectory = $compilerRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $CompilerArguments) { $startInfo.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($startInfo)
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    # Feed text through stdin, matching Windhawk's compilerUtils.ts. This also
    # supports source files protected by local transparent file encryption.
    $process.StandardInput.Write([IO.File]::ReadAllText($SourcePath))
    $process.StandardInput.Close()
    $process.WaitForExit()
    [IO.File]::WriteAllText((Join-Path $logs ($LogName + '.stdout.txt')), $stdout.GetAwaiter().GetResult())
    [IO.File]::WriteAllText((Join-Path $logs ($LogName + '.stderr.txt')), $stderr.GetAwaiter().GetResult())
    $compilerExitCode = $process.ExitCode
    $process.Dispose()
    if ($compilerExitCode -ne 0) { throw "$LogName compiler exited $compilerExitCode; see $logs" }
}
Invoke-Compiler $helperArgs (Join-Path $PSScriptRoot 'StartHost.cpp') 'helper'
Invoke-Compiler $modArgs (Join-Path $ArtifactRoot 'windows-11-start-menu-styler.wh.cpp') 'mod'
$modRuntime = Join-Path $compilerRoot 'x86_64-w64-mingw32\bin'
Copy-Item -LiteralPath (Join-Path $modRuntime 'libc++.dll') -Destination (Join-Path $mods 'libc++.whl') -Force
Copy-Item -LiteralPath (Join-Path $modRuntime 'libunwind.dll') -Destination (Join-Path $mods 'libunwind.whl') -Force
Copy-Item -LiteralPath (Join-Path $modRuntime 'windhawk-mod-shim.dll') -Destination $mods -Force
[ordered]@{ helper = $helperArgs; mod = $modArgs; compiler = $clang } |
    ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $logs 'commands.json') -Encoding utf8
Write-Output "Built $runtime\TaskGlass.StartHost.exe and Data\Mods\64\TaskGlass.StartMenuStyler.dll"
