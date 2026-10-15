[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$scriptPath = Join-Path $PSScriptRoot 'Get-Cloudflared.ps1'
$targetsPath = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($PSScriptRoot, '..', 'apps', 'desktop', 'Sovereign.Desktop', 'RoomTunnel.targets'))
if (-not (Test-Path -LiteralPath $scriptPath -PathType Leaf)) { throw 'Get-Cloudflared.ps1 is missing.' }
if (-not (Test-Path -LiteralPath $targetsPath -PathType Leaf)) { throw 'RoomTunnel.targets is missing.' }

$failures = New-Object System.Collections.Generic.List[string]

function Add-Failure([string]$Text) {
    [void]$script:failures.Add($Text)
    Write-Output "FAIL $Text"
}

function Assert-Throws([scriptblock]$Action, [string]$Pattern, [string]$Name) {
    $threw = $false
    $message = ''
    try {
        & $Action | Out-Null
    } catch {
        $threw = $true
        $message = $_.Exception.Message
        if ([string]::IsNullOrWhiteSpace($message)) { $message = "$_" }
    }
    if (-not $threw) {
        Add-Failure "$Name did not fail."
        return
    }
    if ($message -notmatch $Pattern) {
        Add-Failure "$Name message '$message' did not match /$Pattern/."
    } else {
        Write-Output "PASS $Name"
    }
}

$work = Join-Path ([System.IO.Path]::GetTempPath()) ('cloudflared-script-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes('room-tunnel-test')
    $good = Join-Path $work 'good.bin'
    [System.IO.File]::WriteAllBytes($good, $bytes)
    $goodHash = (Get-FileHash -LiteralPath $good -Algorithm SHA256).Hash.ToLowerInvariant()
    $bad = Join-Path $work 'bad.bin'
    [System.IO.File]::WriteAllBytes($bad, [byte[]](1, 2, 3, 4))
    $missing = Join-Path $work 'missing.exe'
    $dest = Join-Path $work 'out' 'cloudflared.exe'

    $url = & $scriptPath -PrintUrl
    if ($url -ne 'https://github.com/cloudflare/cloudflared/releases/download/2026.9.1/cloudflared-windows-amd64.exe') {
        Add-Failure "PrintUrl returned '$url'."
    } else {
        Write-Output 'PASS print pinned download url'
    }

    Assert-Throws { & $scriptPath -VerifyOnly -Destination $missing -ExpectedSha256 $goodHash } 'cloudflared\.exe is missing' 'verify missing file'
    Assert-Throws { & $scriptPath -VerifyOnly -Destination $bad -ExpectedSha256 $goodHash } 'SHA-256 mismatch' 'verify hash mismatch'
    $verified = & $scriptPath -VerifyOnly -Destination $good -ExpectedSha256 $goodHash
    if ($verified -notmatch [regex]::Escape($goodHash)) {
        Add-Failure "verify match output '$verified' did not contain the hash."
    } else {
        Write-Output 'PASS verify hash match'
    }

    Assert-Throws { & $scriptPath -InputFile $bad -Destination $dest -ExpectedSha256 $goodHash } 'SHA-256 mismatch' 'install rejects mismatched bytes'
    if (Test-Path -LiteralPath $dest) { Add-Failure 'mismatched install wrote cloudflared.exe.' }
    $partial = $dest + '.partial'
    if (Test-Path -LiteralPath $partial) { Add-Failure 'mismatched install left a partial file.' }
    if (-not (Test-Path -LiteralPath $dest) -and -not (Test-Path -LiteralPath $partial)) {
        Write-Output 'PASS mismatched install leaves no file'
    }

    $installed = & $scriptPath -InputFile $good -Destination $dest -ExpectedSha256 $goodHash
    if ($installed -notmatch [regex]::Escape($goodHash)) { Add-Failure "install output '$installed' did not contain the hash." }
    $installedHash = (Get-FileHash -LiteralPath $dest -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($installedHash -ne $goodHash) { Add-Failure "installed hash $installedHash did not match." } else { Write-Output 'PASS install matching bytes' }
    $again = & $scriptPath -InputFile $bad -Destination $dest -ExpectedSha256 $goodHash
    if ($again -notmatch 'already present') { Add-Failure "second install output '$again' did not keep the verified file." }
    $kept = (Get-FileHash -LiteralPath $dest -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($kept -ne $goodHash) { Add-Failure 'a later mismatched input replaced a verified file.' } else { Write-Output 'PASS verified file is kept' }

    $wrongPin = Join-Path $work 'wrong.pin'
    @(
        'version=2026.9.1'
        'asset=cloudflared-windows-amd64.exe'
        'sha256=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
    ) | Set-Content -LiteralPath $wrongPin -Encoding utf8
    Assert-Throws { & $scriptPath -PinFile $wrongPin -PrintUrl } 'RoomTunnel\.cs does not contain pinned cloudflared SHA-256' 'pin hash must match RoomTunnel.cs'

    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $dotnet) {
        Add-Failure 'dotnet was not on PATH, so the Release target check did not run.'
    } else {
        $projDir = Join-Path $work 'msbuild'
        New-Item -ItemType Directory -Path $projDir | Out-Null
        $present = Join-Path $projDir 'present.exe'
        [System.IO.File]::WriteAllBytes($present, [byte[]](0))
        $absent = Join-Path $projDir 'absent.exe'
        @'
namespace RoomTunnelCheck;
public class Marker {}
'@ | Set-Content -LiteralPath (Join-Path $projDir 'Marker.cs') -Encoding utf8
        @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <Import Project="$targetsPath" />
</Project>
"@ | Set-Content -LiteralPath (Join-Path $projDir 'check.csproj') -Encoding utf8

        function Invoke-Target([string]$Configuration, [string]$Exe, [string]$Name) {
            $output = & dotnet build (Join-Path $projDir 'check.csproj') -c $Configuration "-p:RoomTunnelExe=$Exe" --nologo --verbosity minimal 2>&1 | Out-String
            return @{ Code = $LASTEXITCODE; Output = $output; Name = $Name }
        }

        $releaseMissing = Invoke-Target 'Release' $absent 'release missing'
        if ($releaseMissing.Code -eq 0 -or $releaseMissing.Output -notmatch 'Release build requires room-tunnel') {
            Add-Failure "Release build with a missing exe exited $($releaseMissing.Code): $($releaseMissing.Output)"
        } else {
            Write-Output 'PASS Release build fails when cloudflared.exe is missing'
        }

        $releasePresent = Invoke-Target 'Release' $present 'release present'
        if ($releasePresent.Code -ne 0) {
            Add-Failure "Release build with the exe present exited $($releasePresent.Code): $($releasePresent.Output)"
        } else {
            Write-Output 'PASS Release build succeeds when cloudflared.exe is present'
        }

        $debugMissing = Invoke-Target 'Debug' $absent 'debug missing'
        if ($debugMissing.Code -ne 0 -or $debugMissing.Output -notmatch 'ROOMTUNNEL001') {
            Add-Failure "Debug build with a missing exe exited $($debugMissing.Code): $($debugMissing.Output)"
        } else {
            Write-Output 'PASS Debug build warns when cloudflared.exe is missing'
        }

        $debugPresent = Invoke-Target 'Debug' $present 'debug present'
        if ($debugPresent.Code -ne 0 -or $debugPresent.Output -match 'ROOMTUNNEL001') {
            Add-Failure "Debug build with the exe present exited $($debugPresent.Code): $($debugPresent.Output)"
        } else {
            Write-Output 'PASS Debug build is quiet when cloudflared.exe is present'
        }
    }
} finally {
    if (Test-Path -LiteralPath $work) {
        Remove-Item -LiteralPath $work -Recurse -Force
    }
}

if ($failures.Count -gt 0) {
    Write-Output ("FAILED {0}" -f $failures.Count)
    exit 1
}
Write-Output 'OK'
exit 0
