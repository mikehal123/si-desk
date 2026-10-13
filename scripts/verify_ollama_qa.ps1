param(
    [Parameter(Mandatory=$true)][string]$Model,
    [Parameter(Mandatory=$true)][string]$Published,
    [Parameter(Mandatory=$true)][string]$Prompts,
    [Parameter(Mandatory=$true)][string]$Output,
    [switch]$CloseWhenDone,
    [switch]$Thinking
)
$ErrorActionPreference = 'Stop'
$Output = [IO.Path]::GetFullPath($Output)
if (Test-Path -LiteralPath $Output) { throw 'Use a new evidence directory for each run.' }
$resident = Invoke-RestMethod http://127.0.0.1:11434/api/ps -TimeoutSec 10
if (@($resident.models).Count -ne 0) { throw 'Another model is resident. Coordinate ownership before running this test.' }
New-Item -ItemType Directory -Path $Output | Out-Null
$profile = Join-Path $env:LOCALAPPDATA 'SovereignAI.DesktopPreview\qa-ollama-qualification\profile'
$gpuCsv = Join-Path $Output 'gpu-samples.csv'
$inventory = [ordered]@{
    utc = [DateTimeOffset]::UtcNow
    model = $Model
    tags = Invoke-RestMethod http://127.0.0.1:11434/api/tags -TimeoutSec 10
    version = Invoke-RestMethod http://127.0.0.1:11434/api/version -TimeoutSec 10
    processor = Get-CimInstance Win32_Processor | Select-Object Name,NumberOfCores,NumberOfLogicalProcessors
    physicalMemoryBytes = (Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory
    initialResidency = $resident
}
$inventory | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $Output 'inventory.json') -Encoding utf8
function Sample-Gpu {
    $csv = & nvidia-smi --query-gpu=timestamp,name,memory.total,memory.used,utilization.gpu --format=csv,noheader,nounits
    $csv | Add-Content -LiteralPath $gpuCsv -Encoding utf8
}
'timestamp,name,memory_total_mib,memory_used_mib,utilization_percent' | Set-Content -LiteralPath $gpuCsv -Encoding utf8
Sample-Gpu
$app = $null
try {
    $qaArguments = @('--mode','preview','--profile',('"' + $profile + '"'),'--ollama-chat-verify',('"' + $Output + '"'),'--ollama-verify-model',$Model,'--ollama-verify-prompts',('"' + $Prompts + '"'))
    if ($Thinking) { $qaArguments += '--ollama-verify-thinking' }
    $app = Start-Process -FilePath (Join-Path $Published 'SovereignAI.DesktopPreview.exe') -ArgumentList $qaArguments -PassThru
    $app.Id | Set-Content -LiteralPath (Join-Path $Output 'qa-process-id.txt')
    $deadline = [DateTime]::UtcNow.AddMinutes(20)
    $lastStage = ''
    while ([DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 500
        Sample-Gpu
        $app.Refresh()
        if ($app.HasExited) { throw "QA app exited: $($app.ExitCode)" }
        $receiptFile = Join-Path $Output 'ollama-qualification.json'
        if (!(Test-Path -LiteralPath $receiptFile)) { continue }
        try { $receipt = Get-Content -Raw -LiteralPath $receiptFile | ConvertFrom-Json } catch { continue }
        if ($receipt.stage -ne $lastStage) { $lastStage = $receipt.stage; Write-Output "$Model : $lastStage ($(@($receipt.cases).Count) captured)" }
        if ($receipt.stage -in @('finished','incomplete')) { break }
    }
    if ($lastStage -ne 'finished') { throw "QA verification did not finish: $lastStage" }
    # Observe the server after cancellation and recovery; leave samples before unloading this test's model.
    1..6 | ForEach-Object { Start-Sleep -Milliseconds 500; Sample-Gpu }
    Invoke-RestMethod http://127.0.0.1:11434/api/ps -TimeoutSec 10 | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $Output 'final-residency.json') -Encoding utf8
    $receipt.runtime | ConvertTo-Json -Depth 5 | Write-Output
} finally {
    if ($CloseWhenDone -and $app -and !$app.HasExited) { [void]$app.CloseMainWindow() }
}
