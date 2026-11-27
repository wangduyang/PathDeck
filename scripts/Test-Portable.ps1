param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$ExpectedVersion)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($Path)
foreach ($name in @('PathDeck.exe','PathDeck.dll','PathDeck.Desktop.dll','PathDeck.runtimeconfig.json','hostfxr.dll','coreclr.dll','PresentationFramework.dll','README.md','LICENSE.md','build-info.json','Modules\Desktop\Engine\ExplorerTAP.dll','Modules\Desktop\Engine\ExplorerHooks.dll','Modules\Desktop\Engine\ProgramLog.dll','Modules\Desktop\Engine\TranslucentTB.exe','Modules\Desktop\Engine\Xaml.dll','Modules\Desktop\Engine\resources.pri')) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $name) -PathType Leaf)) { throw "Portable package missing: $name" }
}
$forbidden = @('pins.json','colors.json','menu.json','appearance.json','win-e.enabled','settings.json','status.json','launcher.json','notifications.json','last-error.txt')
foreach ($file in Get-ChildItem -LiteralPath $root -File -Recurse) {
    # This checked-in file configures the bundled engine, rather than a user's desktop preferences.
    if ([IO.Path]::GetRelativePath($root, $file.FullName) -eq 'Modules\Desktop\Engine\settings.json') {
        $defaults=Join-Path $PSScriptRoot '..\modules\Desktop\Runtime\Engine\settings.json'
        if ([IO.File]::ReadAllText($file.FullName) -ne [IO.File]::ReadAllText($defaults)) { throw 'Bundled engine defaults were modified.' }
        continue
    }
    if ($file.Name -in $forbidden -or $file.Extension -in @('.log','.tmp') -or $file.Name -like '*.user' -or
        [IO.Path]::GetRelativePath($root, $file.FullName) -like 'Modules\Desktop\Data\*') { throw "Local state in portable package: $($file.FullName)" }
}
$metadata = Get-Content -LiteralPath (Join-Path $root 'build-info.json') -Raw | ConvertFrom-Json
if ($metadata.Version -ne $ExpectedVersion -or -not $metadata.BuildId -or -not $metadata.SelfContained) { throw 'Invalid build metadata.' }
foreach ($name in @('PathDeck.dll','PathDeck.Desktop.dll')) {
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $root $name)).ProductVersion
    if ($version -ne "$ExpectedVersion+$($metadata.BuildId)") { throw "Assembly/build metadata mismatch: $name" }
}
$readme = [IO.File]::ReadAllText((Join-Path $root 'README.md'))
if (-not $readme.StartsWith('# PathDeck') -or -not $readme.Contains('文件') -or $readme.Contains([char]0xFFFD)) { throw 'README is not readable plaintext UTF-8.' }
Write-Output "PASS: clean self-contained portable package, matching assembly versions/build ID, no personal settings or logs, readable Chinese README."
