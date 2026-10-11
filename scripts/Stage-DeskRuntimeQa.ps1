param(
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [Parameter(Mandatory=$true)][string]$BundledPythonDirectory,
    [Parameter(Mandatory=$true)][string]$DependencyDirectory
)
$ErrorActionPreference = 'Stop'
$qaRoot = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $qaRoot) { throw 'Use a fresh QA component directory.' }
if (-not (Test-Path -LiteralPath (Join-Path $BundledPythonDirectory 'python312._pth'))) { throw 'Embedded CPython 3.12 required.' }
if (-not (Test-Path -LiteralPath (Join-Path $DependencyDirectory 'jsonschema'))) { throw 'Staged jsonschema dependencies required.' }
New-Item -ItemType Directory -Path $qaRoot | Out-Null
Copy-Item -LiteralPath $BundledPythonDirectory -Destination (Join-Path $qaRoot 'python') -Recurse
$deskTarget = Join-Path $qaRoot 'desk'
New-Item -ItemType Directory -Path $deskTarget | Out-Null
Copy-Item -LiteralPath $DependencyDirectory -Destination (Join-Path $deskTarget 'dependencies') -Recurse
$deskSource = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\apps\desktop\desk'))
foreach ($subtree in @('contracts','runtime','store','migrations','tools','evidence','workflows')) {
    $from = Join-Path $deskSource $subtree
    Get-ChildItem -LiteralPath $from -File -Recurse | Where-Object { $_.FullName -notmatch '[\\/]__pycache__[\\/]' } | ForEach-Object {
        $target = Join-Path $deskTarget ([IO.Path]::GetRelativePath($deskSource, $_.FullName))
        New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
        Copy-Item -LiteralPath $_.FullName -Destination $target
    }
}
# The identity is itself covered by the compiled runtime inventory. Dirty source
# remains usable for isolated QA but is refused by release packaging.
$sourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sourceCommit = (& git -C $sourceRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $sourceCommit -notmatch '^[0-9a-f]{40}$') { throw 'Cannot identify runtime source commit.' }
$sourceChanges = @(& git -C $sourceRoot status --porcelain --untracked-files=all -- apps/desktop/desk)
if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect runtime source state.' }
@{ schema_version=1; source_commit=$sourceCommit; source_dirty=($sourceChanges.Count -ne 0) } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $deskTarget 'runtime\build-info.json') -Encoding utf8
# Local QA inventory only. Release startup must use authenticated manifest data.
$inventory = @(Get-ChildItem -LiteralPath (Join-Path $qaRoot 'python'),$deskTarget -File -Recurse | ForEach-Object {
    @{ Path = [IO.Path]::GetRelativePath($qaRoot, $_.FullName); Bytes = $_.Length; Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$inventory | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $qaRoot 'qa-files.json') -Encoding utf8
Write-Output "QA component staged: $qaRoot ($($inventory.Count) files)"
