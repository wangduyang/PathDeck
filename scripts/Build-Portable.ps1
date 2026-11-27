param([string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$project = Join-Path $projectRoot 'src\QuickFileDesk.csproj'
$version = (& $Dotnet msbuild $project -nologo -getProperty:Version | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $version -notmatch '^\d+\.\d+\.\d+(-[A-Za-z0-9.-]+)?$') { throw 'Could not read the project version.' }
$buildId = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [guid]::NewGuid().ToString('N').Substring(0,6)
$name = "PathDeck-$version-win-x64-$buildId"
$output = Join-Path $projectRoot "artifacts\$name"
# A fresh destination prevents stale assemblies or local preferences entering a new build.
New-Item -ItemType Directory -Path $output | Out-Null
& $Dotnet publish $project -c Release -r win-x64 --self-contained true -p:InformationalVersion="$version+$buildId" -o $output --nologo
if ($LASTEXITCODE -ne 0) { throw 'PathDeck publish failed. The incomplete staging directory was retained for diagnosis.' }
foreach ($file in @('LICENSE.md','README.md')) { Copy-Item -LiteralPath (Join-Path $projectRoot $file) -Destination $output }
@{ Version=$version; BuildId=$buildId; BuiltAtUtc=[DateTime]::UtcNow.ToString('o'); Runtime='win-x64'; SelfContained=$true } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'build-info.json') -Encoding utf8
& (Join-Path $PSScriptRoot 'Test-Portable.ps1') -Path $output -ExpectedVersion $version
# ZipFile runs in PowerShell so protected Markdown is read as plaintext on this workstation.
$archive = "$output.zip"
[IO.Compression.ZipFile]::CreateFromDirectory($output, $archive, [IO.Compression.CompressionLevel]::Optimal, $false)
$zip = [IO.Compression.ZipFile]::OpenRead($archive)
try {
    $entry = $zip.GetEntry('README.md')
    if ($null -eq $entry -or $null -eq $zip.GetEntry('PathDeck.exe')) { throw 'ZIP root layout is invalid.' }
    $reader = [IO.StreamReader]::new($entry.Open(), [Text.UTF8Encoding]::new($false,$true))
    try { $readme = $reader.ReadToEnd() } finally { $reader.Dispose() }
    if (-not $readme.StartsWith('# PathDeck') -or -not $readme.Contains('文件')) { throw 'ZIP README is not plaintext UTF-8.' }
} finally { $zip.Dispose() }
$checksum = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
"$checksum  $name.zip" | Set-Content -LiteralPath "$archive.sha256" -Encoding ascii
[pscustomobject]@{ Directory=$output; Archive=$archive; Sha256=$checksum; Version=$version; BuildId=$buildId }
