param([Parameter(Mandatory)][string]$Package, [Parameter(Mandatory)][string]$Version)
$ErrorActionPreference='Stop'
$validator=Join-Path $PSScriptRoot '..\scripts\Test-Portable.ps1'
& $validator -Path $Package -ExpectedVersion $Version
function Expect-Rejected([string]$Name, [scriptblock]$Action) {
    $rejected=$false
    try { & $Action } catch { $rejected=$true }
    if (-not $rejected) { throw "Invalid package accepted: $Name" }
}
Expect-Rejected 'mismatched version' { & $validator -Path $Package -ExpectedVersion '0.0.0-invalid' }
$config=Join-Path $Package 'pins.json'
if (Test-Path -LiteralPath $config) { throw 'Validation fixture must start without personal configuration.' }
try {
    [IO.File]::WriteAllText($config,'{}')
    Expect-Rejected 'personal configuration' { & $validator -Path $Package -ExpectedVersion $Version }
} finally {
    # Only the synthetic file created above is removed. Never touch installed application data.
    [IO.File]::Delete($config)
}
$errorLog=Join-Path $Package 'Modules\Desktop\last-error.txt'
if (Test-Path -LiteralPath $errorLog) { throw 'Validation fixture must start without application error logs.' }
try {
    [IO.File]::WriteAllText($errorLog,'Synthetic exception log')
    Expect-Rejected 'application error log' { & $validator -Path $Package -ExpectedVersion $Version }
} finally { [IO.File]::Delete($errorLog) }
$runtime=Join-Path $Package 'coreclr.dll'
$hold=Join-Path $Package 'coreclr.dll.check-hold'
if (Test-Path -LiteralPath $hold) { throw 'A previous runtime validation fixture needs recovery.' }
try {
    Move-Item -LiteralPath $runtime -Destination $hold
    Expect-Rejected 'missing self-contained runtime' { & $validator -Path $Package -ExpectedVersion $Version }
} finally { if (Test-Path -LiteralPath $hold) { Move-Item -LiteralPath $hold -Destination $runtime } }
& $validator -Path $Package -ExpectedVersion $Version
Write-Output 'PASS: portable validation rejects version mismatch, personal settings, application error logs and missing runtime; all fixture changes restored.'
