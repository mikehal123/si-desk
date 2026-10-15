[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Directory
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($Directory)) {
    throw 'Publish directory is required.'
}

$root = [System.IO.Path]::GetFullPath($Directory)
if (-not (Test-Path -LiteralPath $root -PathType Container)) {
    throw "Publish directory was not found: $root"
}

$pdbs = @(Get-ChildItem -LiteralPath $root -Recurse -File | Where-Object { $_.Extension -eq '.pdb' })
foreach ($pdb in $pdbs) {
    Remove-Item -LiteralPath $pdb.FullName -Force
}

$left = @(Get-ChildItem -LiteralPath $root -Recurse -File | Where-Object { $_.Extension -eq '.pdb' })
if ($left.Count -gt 0) {
    throw "Release output still contains $($left.Count) .pdb file(s)."
}
