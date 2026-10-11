param(
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [string]$Dotnet = 'dotnet',
    [string]$RuntimeFrameworkVersion = '10.0.11'
)
$ErrorActionPreference = 'Stop'
$stageRoot = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $stageRoot) { throw 'Use a fresh document helper staging directory.' }
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\apps\desktop\Sovereign.Desktop.DocumentExtraction\Sovereign.Desktop.DocumentExtraction.csproj'))
$component = Join-Path $stageRoot 'document-extractor'
New-Item -ItemType Directory -Path $stageRoot | Out-Null
& $Dotnet restore $project --runtime win-x64 --ignore-failed-sources "-p:RuntimeFrameworkVersion=$RuntimeFrameworkVersion"
if ($LASTEXITCODE -ne 0) { throw 'Document helper restore failed.' }
& $Dotnet publish $project --configuration Release --runtime win-x64 --self-contained true --no-restore --output $component "-p:RuntimeFrameworkVersion=$RuntimeFrameworkVersion" --nologo --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw 'Document helper publish failed.' }
foreach ($name in @('Sovereign.Desktop.DocumentExtraction.exe', 'hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $component $name))) { throw "Self-contained helper is missing $name" }
}
$inventory = @(Get-ChildItem -LiteralPath $component -File -Recurse | ForEach-Object {
    if (($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Document helper links are not permitted.' }
    @{ Path=[IO.Path]::GetRelativePath($component, $_.FullName); Bytes=$_.Length; Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$inventoryPath = Join-Path $stageRoot 'document-extraction-files.json'
$inventory | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $inventoryPath -Encoding utf8
Write-Output "Document helper staged: $component ($($inventory.Count) files). Embed $inventoryPath with DocumentExtractionInventory and include both in the publish directory."
