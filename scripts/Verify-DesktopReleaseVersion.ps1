param(
    [Parameter(Mandatory=$true)][string]$AppDirectory,
    [Parameter(Mandatory=$true)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$ExpectedVersion
)
$ErrorActionPreference = 'Stop'
foreach ($name in @('SovereignAI.DesktopPreview.exe', 'SovereignAI.DesktopPreview.dll', 'Sovereign.Desktop.Presentation.dll')) {
    $path = Join-Path $AppDirectory $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing release binary: $name" }
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($path)
    $productRelease = ($version.ProductVersion -split '[+-]')[0]
    if ($version.FileVersion -ne "$ExpectedVersion.0" -or $productRelease -ne $ExpectedVersion) {
        throw "Release version mismatch in ${name}: file=$($version.FileVersion), product=$($version.ProductVersion), expected=$ExpectedVersion. Packaging refused."
    }
}
Write-Output "Release executable, assembly and displayed version agree: $ExpectedVersion"
