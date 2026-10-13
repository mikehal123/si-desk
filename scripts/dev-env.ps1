$root = Split-Path -Parent $PSScriptRoot
$local = Join-Path $root ".tools\dotnet\dotnet.exe"
$dotnet = $null
if (Test-Path -LiteralPath $local) {
    $env:DOTNET_ROOT = Split-Path -Parent $local
    $env:PATH = "$env:DOTNET_ROOT;$env:PATH"
    $dotnet = $local
} else {
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) {
        $dotnet = $cmd.Source
        $env:DOTNET_ROOT = Split-Path -Parent $dotnet
    } else {
        $userDotnet = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"
        if (Test-Path -LiteralPath $userDotnet) {
            $dotnet = $userDotnet
            $env:DOTNET_ROOT = Split-Path -Parent $userDotnet
            $env:PATH = "$env:DOTNET_ROOT;$env:PATH"
        }
    }
}
if ($dotnet -and -not (Test-Path -LiteralPath (Join-Path (Split-Path -Parent $dotnet) 'sdk'))) {
    foreach ($candidate in @(
        (Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'),
        (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe')
    )) {
        if (Test-Path -LiteralPath $candidate) {
            $dotnet = $candidate
            $env:DOTNET_ROOT = Split-Path -Parent $dotnet
            $env:PATH = "$env:DOTNET_ROOT;$env:PATH"
            break
        }
    }
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"
if (-not $dotnet) {
    Write-Error "dotnet was not found. Install the .NET SDK, put dotnet on PATH, or place dotnet.exe at .tools\dotnet\dotnet.exe."
    exit 1
}
& $dotnet @args
if ($null -ne $LASTEXITCODE) { exit $LASTEXITCODE }
