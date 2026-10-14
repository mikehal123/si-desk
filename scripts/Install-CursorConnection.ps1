param([string]$ArchivePath)
$ErrorActionPreference='Stop'
$version='2026.09.23-86fc751'
$hash='9c7232600ca77e7a6327dc9a0036277bd57b66923c6b65fc7abe9d957eb728e2'
$root=Join-Path $env:LOCALAPPDATA 'SovereignAI.Tools\CursorAgent'
$target=Join-Path $root $version
if(Test-Path -LiteralPath $target) { throw "Cursor runtime already exists at $target. It was not overwritten." }
New-Item -ItemType Directory -Path $root -Force | Out-Null
$stage=Join-Path $root ('download-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null
if(!$ArchivePath) {
    $ArchivePath=Join-Path $stage 'official-package.zip'
    Invoke-WebRequest -UseBasicParsing -Uri "https://downloads.cursor.com/lab/$version/windows/x64/agent-cli-package.zip" -OutFile $ArchivePath
}
if((Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $hash) { throw 'Official Cursor package does not match the pinned download.' }
$extract=Join-Path $stage 'runtime'
Expand-Archive -LiteralPath $ArchivePath -DestinationPath $extract
foreach($name in @('node.exe','index.js')) { if(!(Test-Path -LiteralPath (Join-Path $extract ('dist-package\'+$name)))) { throw 'Cursor package is incomplete.' } }
# Both paths are fixed children of the application-owned tools directory. No existing runtime is replaced.
$allowed=[IO.Path]::GetFullPath($root).TrimEnd('\')+'\'
foreach($path in @($extract,$target)) { if(![IO.Path]::GetFullPath($path).StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)) { throw 'Runtime path escapes tools directory.' } }
Move-Item -LiteralPath $extract -Destination $target
@{ version=$version; source="https://downloads.cursor.com/lab/$version/windows/x64/agent-cli-package.zip"; archive_sha256=$hash } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $target 'installation.json') -Encoding UTF8
Write-Host 'Cursor connection installed. Open SI Desk, New chat, Cursor, then Sign in with Cursor. Uses your own Cursor account and limits. No PATH changes.'
