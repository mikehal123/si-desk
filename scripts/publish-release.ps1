[CmdletBinding()]
param(
    [string]$ScanScript
)

$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

$propsPath = Join-Path $repoRoot 'Directory.Build.props'
if (-not (Test-Path -LiteralPath $propsPath -PathType Leaf)) {
    throw 'Directory.Build.props was not found in the repo root.'
}

[xml]$propsXml = Get-Content -LiteralPath $propsPath -Raw
$versionNode = $propsXml.SelectSingleNode('//Version')
if ($null -eq $versionNode -or [string]::IsNullOrWhiteSpace($versionNode.InnerText)) {
    throw 'Directory.Build.props has no Version value.'
}

$version = $versionNode.InnerText.Trim()
if ($version -notmatch '^[0-9]+(\.[0-9]+){1,3}$') {
    throw 'Directory.Build.props Version must be a numeric product version.'
}

if ([string]::IsNullOrWhiteSpace($ScanScript)) {
    $ScanScript = Join-Path $PSScriptRoot 'public-scan.ps1'
    if (-not (Test-Path -LiteralPath $ScanScript -PathType Leaf)) {
        throw 'No public scan script is available. Add scripts\public-scan.ps1 next to this script, or pass -ScanScript.'
    }
} elseif (-not [System.IO.Path]::IsPathRooted($ScanScript)) {
    $fromRepo = Join-Path $repoRoot $ScanScript
    $fromHere = Join-Path $PSScriptRoot $ScanScript
    if (Test-Path -LiteralPath $fromRepo -PathType Leaf) {
        $ScanScript = $fromRepo
    } elseif (Test-Path -LiteralPath $fromHere -PathType Leaf) {
        $ScanScript = $fromHere
    } else {
        throw 'No public scan script is available. Pass -ScanScript pointing at a scan script inside this repo, or add scripts\public-scan.ps1.'
    }
} elseif (-not (Test-Path -LiteralPath $ScanScript -PathType Leaf)) {
    throw 'No public scan script is available at the -ScanScript path.'
}

$scanPath = [System.IO.Path]::GetFullPath($ScanScript)

$projectPath = Join-Path $repoRoot 'apps\desktop\Sovereign.Desktop\Sovereign.Desktop.csproj'
if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    throw 'Desktop project file was not found.'
}

$publishDir = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts\publish\win-x64'))
$repoPrefix = $repoRoot.TrimEnd('\') + '\'
if (-not $publishDir.StartsWith($repoPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Publish folder resolved outside the repo.'
}
$expectedEnding = [System.IO.Path]::Combine('artifacts', 'publish', 'win-x64')
if (-not $publishDir.EndsWith($expectedEnding, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Publish folder is not artifacts\publish\win-x64 under the repo root.'
}

if (Test-Path -LiteralPath $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}
New-Item -ItemType Directory -Path $publishDir | Out-Null

& dotnet publish $projectPath -c Release -r win-x64 --self-contained true -o $publishDir
if ($LASTEXITCODE -ne 0) {
    throw "Publish failed with exit code $LASTEXITCODE."
}

$exePath = Join-Path $publishDir 'xAIDesk.exe'
if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
    throw 'Publish did not produce xAIDesk.exe.'
}

$xbfPath = Join-Path $publishDir 'App.xbf'
if (-not (Test-Path -LiteralPath $xbfPath -PathType Leaf)) {
    throw 'Publish did not produce App.xbf. WinUI skips that copy when publish uses -o.'
}

$setupProject = Join-Path $repoRoot 'apps\desktop\Sovereign.Desktop.PreviewSetup\Sovereign.Desktop.PreviewSetup.csproj'
$setupDir = Join-Path $repoRoot 'artifacts\publish\setup'
if (Test-Path -LiteralPath $setupDir) {
    Remove-Item -LiteralPath $setupDir -Recurse -Force
}
New-Item -ItemType Directory -Path $setupDir | Out-Null
& dotnet publish $setupProject -c Release -r win-x64 --self-contained true -o $setupDir
if ($LASTEXITCODE -ne 0) {
    throw "Setup publish failed with exit code $LASTEXITCODE."
}
# Publish names the exe after PreviewSetup's AssemblyName. The zip still
# ships xAIDesk.Setup.exe: SetupEngine only accepts a process path that ends
# with that file name, and that is the installer users run.
$setupOutputName = 'SovereignAI.DesktopPreview.Setup.exe'
$setupExe = Join-Path $setupDir $setupOutputName
if (-not (Test-Path -LiteralPath $setupExe -PathType Leaf)) {
    throw "Setup publish did not produce $setupOutputName."
}
Copy-Item -LiteralPath $setupExe -Destination (Join-Path $publishDir 'xAIDesk.Setup.exe') -Force

$scanOutput = @(& $scanPath -Root $publishDir 2>&1)
$scanExit = $LASTEXITCODE

$hitLines = New-Object System.Collections.Generic.List[string]
foreach ($item in $scanOutput) {
    foreach ($part in ([string]$item -split '\r?\n')) {
        if ($part -match '^hits: \d+$') {
            $hitLines.Add($part)
        }
    }
}

if ($hitLines.Count -eq 0) {
    throw 'Public scan did not print a hits line.'
}

$lastHits = $hitLines[$hitLines.Count - 1]
if ($lastHits -ne 'hits: 0') {
    throw "Public scan failed ($lastHits)."
}

if ($scanExit -ne 0) {
    throw "Public scan failed with exit code $scanExit."
}

$zipPath = Join-Path $repoRoot ("xAIDesk-{0}-win-x64.zip" -f $version)
if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

if (-not ('System.IO.Compression.ZipFile' -as [type])) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
}
[System.IO.Compression.ZipFile]::CreateFromDirectory($publishDir, $zipPath)

$zipItem = Get-Item -LiteralPath $zipPath
Write-Output $zipItem.FullName
Write-Output $zipItem.Length
