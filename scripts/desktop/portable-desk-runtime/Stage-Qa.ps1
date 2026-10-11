param([Parameter(Mandatory=$true)][string]$VerifiedReleasePayload, [Parameter(Mandatory=$true)][string]$Output)
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $Output) { throw 'QA component staging requires a fresh output directory.' }
$source = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$desk = Join-Path $Output 'desk'
New-Item -ItemType Directory -Path $desk -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $VerifiedReleasePayload 'engine-bundle/python') -Destination (Join-Path $Output 'python') -Recurse
Copy-Item -LiteralPath (Join-Path $VerifiedReleasePayload 'desk/dependencies') -Destination $desk -Recurse
foreach ($folder in @('runtime','contracts')) {
    $target = Join-Path $desk $folder
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $source "apps/desktop/desk/$folder") -File |
        Where-Object { $_.Extension -in @('.py','.json') -and $_.Name -notmatch '^(test_|build_)' } |
        Copy-Item -Destination $target
}
# QA inventory only; not the production component-manifest contract or a release fragment.
Get-ChildItem -LiteralPath $Output -File -Recurse | ForEach-Object {
    @{path=[IO.Path]::GetRelativePath($Output,$_.FullName);bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Output 'qa-inventory.json') -Encoding utf8
