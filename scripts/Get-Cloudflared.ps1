[CmdletBinding()]
param(
    [string]$Destination,
    [string]$PinFile,
    [string]$ExpectedSha256,
    [string]$Version,
    [string]$Asset,
    [string]$InputFile,
    [switch]$VerifyOnly,
    [switch]$PrintUrl
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($PinFile)) {
    $PinFile = [System.IO.Path]::Combine($repoRoot, 'apps', 'desktop', 'room-tunnel', 'cloudflared.pin')
}
if ([string]::IsNullOrWhiteSpace($Destination)) {
    $Destination = [System.IO.Path]::Combine($repoRoot, 'apps', 'desktop', 'room-tunnel', 'cloudflared.exe')
}

function Read-CloudflaredPin([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Pinned cloudflared file was not found at '$Path'."
    }
    $map = @{}
    foreach ($line in Get-Content -LiteralPath $Path) {
        $trim = $line.Trim()
        if ($trim.Length -eq 0 -or $trim.StartsWith('#')) { continue }
        $parts = $trim.Split('=', 2)
        if ($parts.Length -ne 2) { continue }
        $map[$parts[0].Trim()] = $parts[1].Trim()
    }
    return $map
}

function Assert-RoomTunnelPinsHash([string]$Root, [string]$Expected) {
    $source = [System.IO.Path]::Combine($Root, 'apps', 'desktop', 'Sovereign.Desktop.Client', 'RoomTunnel.cs')
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw 'RoomTunnel.cs was not found.'
    }
    $text = Get-Content -LiteralPath $source -Raw
    if ($text.IndexOf($Expected, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "RoomTunnel.cs does not contain pinned cloudflared SHA-256 $Expected."
    }
}

function Get-VerifiedCloudflaredHash([string]$Path, [string]$Expected) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "cloudflared.exe is missing at '$Path'."
    }
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    $want = $Expected.ToLowerInvariant()
    if ($actual -ne $want) {
        throw "cloudflared SHA-256 mismatch. Expected $want but computed $actual."
    }
    return $actual
}

$pin = Read-CloudflaredPin $PinFile
$explicitHash = $PSBoundParameters.ContainsKey('ExpectedSha256') -and -not [string]::IsNullOrWhiteSpace($ExpectedSha256)
if ([string]::IsNullOrWhiteSpace($Version)) { $Version = [string]$pin['version'] }
if ([string]::IsNullOrWhiteSpace($Asset)) { $Asset = [string]$pin['asset'] }
if ([string]::IsNullOrWhiteSpace($ExpectedSha256)) { $ExpectedSha256 = [string]$pin['sha256'] }

if ($Version -notmatch '^\d{4}\.\d+\.\d+$') {
    throw "Pinned cloudflared version '$Version' is not a cloudflared release tag."
}
if ($Asset -ne 'cloudflared-windows-amd64.exe') {
    throw 'Pinned cloudflared asset must be cloudflared-windows-amd64.exe.'
}
if ($ExpectedSha256 -notmatch '^[0-9a-fA-F]{64}$') {
    throw 'Pinned cloudflared SHA-256 must be 64 hex characters.'
}
if (-not $explicitHash) {
    Assert-RoomTunnelPinsHash $repoRoot $ExpectedSha256
}

$downloadUri = "https://github.com/cloudflare/cloudflared/releases/download/$Version/$Asset"
if ($PrintUrl) {
    Write-Output $downloadUri
    return
}

if ($VerifyOnly) {
    $verified = Get-VerifiedCloudflaredHash $Destination $ExpectedSha256
    Write-Output "cloudflared SHA-256 verified $verified"
    return
}

if (Test-Path -LiteralPath $Destination -PathType Leaf) {
    $existing = (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($existing -eq $ExpectedSha256.ToLowerInvariant()) {
        Write-Output "Pinned cloudflared $Version already present."
        return
    }
}

$parent = Split-Path -Parent $Destination
if (-not [string]::IsNullOrWhiteSpace($parent) -and -not (Test-Path -LiteralPath $parent)) {
    New-Item -ItemType Directory -Path $parent | Out-Null
}
$partial = $Destination + '.partial'
if (Test-Path -LiteralPath $partial) {
    Remove-Item -LiteralPath $partial -Force
}
try {
    if (-not [string]::IsNullOrWhiteSpace($InputFile)) {
        if (-not (Test-Path -LiteralPath $InputFile -PathType Leaf)) {
            throw "cloudflared input file was not found at '$InputFile'."
        }
        Copy-Item -LiteralPath $InputFile -Destination $partial -Force
    } else {
        try {
            Invoke-WebRequest -Uri $downloadUri -OutFile $partial -UseBasicParsing
        } catch {
            throw "Could not download pinned cloudflared $Version from $downloadUri. $($_.Exception.Message)"
        }
    }
    $hash = Get-VerifiedCloudflaredHash $partial $ExpectedSha256
    Move-Item -LiteralPath $partial -Destination $Destination -Force
    Write-Output "Pinned cloudflared $Version SHA-256 $hash"
} finally {
    if (Test-Path -LiteralPath $partial) {
        Remove-Item -LiteralPath $partial -Force
    }
}
