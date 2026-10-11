param(
    [Parameter(Mandatory=$true)][string]$Path,
    [Parameter(Mandatory=$true)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$ExpectedVersion
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw 'Fresh installer is missing.' }
$identity = [Diagnostics.FileVersionInfo]::GetVersionInfo([IO.Path]::GetFullPath($Path))
if ($identity.FileVersion -ne "$ExpectedVersion.0" -or ($identity.ProductVersion -split '[+-]')[0] -ne $ExpectedVersion) {
    throw 'Fresh installer version does not match the application version.'
}
Write-Output "Fresh installer version: $ExpectedVersion"
