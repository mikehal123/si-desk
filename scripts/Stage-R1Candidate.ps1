param(
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [Parameter(Mandatory=$true)][string]$PreviousPackage,
    [Parameter(Mandatory=$true)][string]$Dotnet,
    [Parameter(Mandatory=$true)][string]$BuildPython,
    [string]$ReleaseNotesFile
)
$ErrorActionPreference = 'Stop'
$source = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
$previous = [IO.Path]::GetFullPath($PreviousPackage)
$previousVersion = [regex]::Match([IO.Path]::GetFileName($previous), '^xAIDesk-Windows-(\d+\.\d+\.\d+)$').Groups[1].Value
if (-not $previousVersion) { throw 'Previous package version is unavailable.' }
if (Test-Path -LiteralPath $output) { throw 'Candidate staging requires a fresh output directory.' }
$changes = @(& git -C $source status --porcelain --untracked-files=all)
if ($LASTEXITCODE -ne 0 -or $changes.Count) { throw 'Commit or account for all source changes before staging a pinned candidate.' }
$pin = (& git -C $source rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $pin -notmatch '^[0-9a-f]{40}$') { throw 'Source pin is unavailable.' }
[xml]$properties = Get-Content -LiteralPath (Join-Path $source 'Directory.Build.props') -Raw
$version = [string]$properties.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Source version is invalid.' }

# Use previously verified pinned runtimes/dependencies without installing from pip.
$priorFiles = Get-Content -LiteralPath (Join-Path $previous 'files.json') -Raw | ConvertFrom-Json
foreach ($row in $priorFiles) {
    $path = [IO.Path]::GetFullPath((Join-Path $previous $row.path))
    if (-not $path.StartsWith($previous.TrimEnd('\')+'\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Prior manifest path escapes package.' }
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -ne $row.bytes -or
        (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $row.sha256) { throw "Prior package differs: $($row.path)" }
}
New-Item -ItemType Directory -Path $output | Out-Null
$worker = Join-Path $output 'worker'
$helper = Join-Path $output 'extraction'
$publish = Join-Path $output 'publish'
$setup = Join-Path $output 'setup'
& (Join-Path $PSScriptRoot 'Stage-DeskRuntimeQa.ps1') -OutputDirectory $worker -BundledPythonDirectory (Join-Path $previous 'payload\engine-bundle\python') -DependencyDirectory (Join-Path $previous 'payload\desk\dependencies')
& (Join-Path $PSScriptRoot 'Stage-DocumentExtraction.ps1') -OutputDirectory $helper -Dotnet $Dotnet
& $Dotnet publish (Join-Path $source 'apps\desktop\Sovereign.Desktop\Sovereign.Desktop.csproj') --configuration Release --runtime win-x64 --self-contained true --no-restore --output $publish --nologo --verbosity minimal "-p:DeskRuntimeInventory=$worker\qa-files.json" "-p:DocumentExtractionInventory=$helper\document-extraction-files.json" "-p:InformationalVersion=$version-r1-candidate-$($pin.Substring(0,12))"
if ($LASTEXITCODE -ne 0) { throw 'Candidate application publish failed.' }
& $Dotnet publish (Join-Path $source 'apps\desktop\Sovereign.Desktop.PreviewSetup\Sovereign.Desktop.PreviewSetup.csproj') --configuration Release --runtime win-x64 --self-contained true --output $setup --nologo --verbosity minimal "-p:InformationalVersion=$version-r1-candidate-$($pin.Substring(0,12))"
if ($LASTEXITCODE -ne 0) { throw 'Candidate installer publish failed.' }
& $Dotnet publish (Join-Path $source 'apps\desktop\Sovereign.Desktop.Updater\Sovereign.Desktop.Updater.csproj') --configuration Release --runtime win-x64 --self-contained true --output (Join-Path $publish 'updater') --nologo --verbosity minimal "-p:InformationalVersion=$version-r1-candidate-$($pin.Substring(0,12))"
if ($LASTEXITCODE -ne 0) { throw 'Candidate updater publish failed.' }
Copy-Item -LiteralPath $worker -Destination (Join-Path $publish 'desk-runtime') -Recurse
& $BuildPython (Join-Path $PSScriptRoot 'desktop_desk_compatibility.py') --publish $publish --version $version
if ($LASTEXITCODE -ne 0) { throw 'Candidate lookup compatibility metadata failed.' }
Get-ChildItem -LiteralPath $helper -Force | Copy-Item -Destination $publish -Recurse
New-Item -ItemType Directory -Path (Join-Path $publish 'engine-bundle') | Out-Null
Copy-Item -LiteralPath (Join-Path $previous 'payload\engine-bundle\python') -Destination (Join-Path $publish 'engine-bundle\python') -Recurse
Copy-Item -LiteralPath (Join-Path $previous 'payload\desk\dependencies') -Destination (Join-Path $publish 'desk\dependencies') -Recurse
Copy-Item -LiteralPath (Join-Path $previous 'payload\valuation-runtime-files.json') -Destination $publish
& (Join-Path $publish 'engine-bundle\python\python.exe') -I -B -c 'import sys; sys.path.insert(0, sys.argv[1]); import jsonschema, rpds; print("Pinned valuation dependencies import successfully")' (Join-Path $publish 'desk\dependencies')
if ($LASTEXITCODE -ne 0) { throw 'Bundled valuation dependencies are incomplete.' }

$notes = Join-Path $output 'candidate-notes.md'
@"
# Isolated R1 development candidate

Source: $pin. Numeric version: $version; this is not a new published release.

Contains saved-evidence draft, separately reviewed valuation, human decision and local review-date work. Live evidence search remains unavailable. Use an isolated qa-* instance. Production profiles and installed main app must stay untouched.

Clean supported Windows, real-provider checks, full investigation/follow-up, actual signed N-to-N+1 update and Caesar acceptance are still open. No paid call or public release is authorized by this package.
"@ | Set-Content -LiteralPath $notes -Encoding utf8
if ($ReleaseNotesFile) { Copy-Item -LiteralPath $ReleaseNotesFile -Destination $notes -Force }
$arguments = @((Join-Path $PSScriptRoot 'Package-DesktopRelease.py'), '--previous', $previous, '--publish', $publish,
    '--output', (Join-Path $output 'package'), '--version', $version, '--previous-version', $previousVersion, '--notes', $notes,
    '--setup', (Join-Path $setup 'SovereignAI.DesktopPreview.Setup.exe'))
foreach ($item in Get-ChildItem -LiteralPath (Join-Path $previous 'payload\engine-bundle') -Force) {
    if ($item.Name -ne 'python') { $arguments += @('--retain-component', ('engine-bundle/' + $item.Name)) }
}
& $BuildPython @arguments
if ($LASTEXITCODE -ne 0) { throw 'Candidate packaging failed.' }
@{source_commit=$pin; version=$version; candidate=$true; source_clean=$true; previous_package=$previous;
    worker_inventory_sha256=(Get-FileHash -LiteralPath (Join-Path $worker 'qa-files.json')).Hash.ToLowerInvariant();
    extraction_inventory_sha256=(Get-FileHash -LiteralPath (Join-Path $helper 'document-extraction-files.json')).Hash.ToLowerInvariant();
    published=$false; signed=$false} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'candidate-source.json') -Encoding utf8
Write-Output "Candidate staged from $pin. This is development packaging evidence, not R1 release qualification."
