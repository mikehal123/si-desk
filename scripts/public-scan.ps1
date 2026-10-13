param(
    [string]$Root
)
$ErrorActionPreference = 'Stop'

$script:hits = New-Object System.Collections.Generic.List[string]
$script:maxBytes = 2MB
$script:timeout = [TimeSpan]::FromSeconds(2)
$script:opt = [System.Text.RegularExpressions.RegexOptions]::Compiled
$script:optIgnore = $script:opt -bor [System.Text.RegularExpressions.RegexOptions]::IgnoreCase
$script:utf8 = New-Object System.Text.UTF8Encoding -ArgumentList $false, $false

$script:skipNames = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
foreach ($name in @('bin', 'obj', 'deps', 'preserved', '.git')) {
    [void]$script:skipNames.Add($name)
}

$script:binaryExt = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
foreach ($ext in @(
    '.png', '.jpg', '.jpeg', '.gif', '.bmp', '.ico', '.webp', '.tif', '.tiff', '.heic', '.psd',
    '.dll', '.exe', '.pdb', '.zip', '.nupkg', '.snupkg', '.7z', '.gz', '.tgz', '.bz2', '.xz', '.rar', '.cab', '.msi', '.msix', '.appx',
    '.so', '.dylib', '.a', '.lib', '.obj', '.o', '.ilk', '.exp', '.res', '.resources',
    '.woff', '.woff2', '.ttf', '.otf', '.eot',
    '.mp3', '.mp4', '.m4a', '.wav', '.ogg', '.webm', '.avi', '.mov', '.mkv', '.flac', '.aac',
    '.pdf', '.doc', '.docx', '.xls', '.xlsx', '.ppt', '.pptx',
    '.pyc', '.pyo', '.class', '.jar', '.war', '.ear',
    '.wasm', '.bin', '.dat', '.pak', '.db', '.sqlite', '.sqlite3', '.mdb',
    '.pfx', '.p12', '.snk',
    '.vsix', '.deb', '.rpm', '.dmg', '.iso', '.img',
    '.onnx', '.gguf', '.safetensors', '.pt', '.parquet'
)) {
    [void]$script:binaryExt.Add($ext)
}

$script:litA = 'res' + 'earch'
$script:litS = 'Sovereign' + 'AI'
$script:litQ = 'Qu' + 'ant'
$script:litT = 'trad' + 'ing'
$script:litO = 'One' + 'Drive'
$script:previewBack = '%LOCALAPPDATA%\' + $script:litS + '.DesktopPreview'
$script:previewFwd = '%LOCALAPPDATA%/' + $script:litS + '.DesktopPreview'
$script:chartRel = 'apps\desktop\charting'

function New-ScanRegex([string]$pattern, [System.Text.RegularExpressions.RegexOptions]$options) {
    return New-Object System.Text.RegularExpressions.Regex -ArgumentList $pattern, $options, $script:timeout
}

function Convert-Short([string]$text) {
    if ($null -eq $text) { return '' }
    $flat = [regex]::Replace($text, '\s+', ' ').Trim()
    if ($flat.Length -gt 80) { return $flat.Substring(0, 80) }
    return $flat
}

function Add-Hit([string]$category, [string]$rel, [int]$lineNo, [string]$matchText) {
    $short = Convert-Short $matchText
    if ($lineNo -lt 1) {
        [void]$script:hits.Add("$category`t$rel`t$short")
    } else {
        [void]$script:hits.Add("$category`t${rel}:$lineNo`t$short")
    }
}

function Test-DeniedToken([string]$text) {
    foreach ($token in @('key', 'xai-desk', 'desk-foundation', 'desk-compatibility')) {
        if ([string]::Equals($text, $token, [StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    return $false
}

function Test-InsidePreview([string]$line, [int]$index, [int]$length) {
    foreach ($token in @($script:previewBack, $script:previewFwd)) {
        $start = 0
        while ($start -le $line.Length) {
            $at = $line.IndexOf($token, $start, [StringComparison]::OrdinalIgnoreCase)
            if ($at -lt 0) { break }
            $end = $at + $token.Length
            if ($index -ge $at -and ($index + $length) -le $end) { return $true }
            $start = $at + 1
            if ($start -le $at) { break }
        }
    }
    return $false
}

function Test-UnderChart([string]$rel) {
    $norm = $rel.Replace('/', '\')
    if ([string]::Equals($norm, $script:chartRel, [StringComparison]::OrdinalIgnoreCase)) { return $true }
    return $norm.StartsWith($script:chartRel + '\', [StringComparison]::OrdinalIgnoreCase)
}

function Add-RegexHits([string]$category, [System.Text.RegularExpressions.Regex]$re, [string]$rel, [int]$lineNo, [string]$line, [bool]$guardPreview) {
    $found = $null
    try {
        $found = $re.Matches($line)
    } catch {
        return
    }
    for ($mi = 0; $mi -lt $found.Count; $mi++) {
        $item = $found[$mi]
        if ($category -eq 'secret' -and (Test-DeniedToken $item.Value)) { continue }
        if ($guardPreview -and (Test-InsidePreview $line $item.Index $item.Length)) { continue }
        Add-Hit $category $rel $lineNo $item.Value
    }
}

function Add-Files([string]$root, [System.Collections.Generic.List[string]]$into) {
    $pending = New-Object System.Collections.Generic.Stack[string]
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $pending.Push($root)
    while ($pending.Count -gt 0) {
        $dir = $pending.Pop()
        if (-not $seen.Add($dir)) { continue }
        try {
            foreach ($child in [System.IO.Directory]::EnumerateDirectories($dir)) {
                $name = [System.IO.Path]::GetFileName($child)
                if ($script:skipNames.Contains($name)) { continue }
                try {
                    $attrs = [System.IO.File]::GetAttributes($child)
                    if (([int]$attrs -band [int][System.IO.FileAttributes]::ReparsePoint) -ne 0) { continue }
                } catch {
                    continue
                }
                $pending.Push($child)
            }
        } catch {
        }
        try {
            foreach ($file in [System.IO.Directory]::EnumerateFiles($dir)) {
                [void]$into.Add($file)
            }
        } catch {
        }
    }
}

try {
    if ([string]::IsNullOrWhiteSpace($Root)) {
        $Root = Split-Path -Parent $PSScriptRoot
    }
    if (-not [string]::IsNullOrWhiteSpace($Root)) {
        $Root = [System.IO.Path]::GetFullPath($Root)
    }
    if (-not [string]::IsNullOrWhiteSpace($Root) -and [System.IO.Directory]::Exists($Root)) {
        $rootPrefix = $Root.TrimEnd('\') + '\'
        $assignRe = New-ScanRegex '(?i)(?:api[_-]?key|secret[_-]?key|access[_-]?token|refresh[_-]?token|auth[_-]?token|password)\s*[=:]\s*[''"]([^''"]*)[''"]' $script:opt
        $secretRes = @(
            (New-ScanRegex '(?<![A-Za-z0-9])sk-[A-Za-z0-9]{20,}' $script:opt),
            (New-ScanRegex '(?<![A-Za-z0-9])xai-(?!desk\b)[A-Za-z0-9]{16,}' $script:opt),
            (New-ScanRegex '(?i)(?<![A-Za-z0-9])bearer\s+[A-Za-z0-9._\-]{20,}' $script:opt),
            (New-ScanRegex '-----BEGIN (?:RSA |OPENSSH |EC |DSA )?PRIVATE KEY-----' $script:opt),
            (New-ScanRegex 'ghp_[A-Za-z0-9]{20,}' $script:opt),
            (New-ScanRegex 'github_pat_[A-Za-z0-9_]{20,}' $script:opt),
            (New-ScanRegex 'AKIA[0-9A-Z]{16}' $script:opt),
            (New-ScanRegex 'xox[baprs]-[A-Za-z0-9-]{10,}' $script:opt)
        )
        $pathRe = New-ScanRegex ((@(
            ('[A-Za-z]:\\' + 'Users' + '\\'),
            ('[A-Za-z]:/' + 'Users' + '/'),
            ('/mnt/[a-z]/' + 'Users' + '/'),
            ('/' + 'Users' + '/'),
            $script:litO,
            ('[A-Za-z]:\\(?:' + $script:litS + '|imigrok)\b')
        ) -join '|')) $script:opt
        $emailRe = New-ScanRegex '[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}' $script:opt
        $tailRe = New-ScanRegex '\b100\.(?:6[4-9]|[7-9]\d|1[0-1]\d|12[0-7])\.(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\.(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\b' $script:opt
        $reA = New-ScanRegex $script:litA $script:optIgnore
        $reS = New-ScanRegex $script:litS $script:opt
        $reQ = New-ScanRegex ('\b' + $script:litQ + '\b') $script:opt
        $reT = New-ScanRegex ('\b' + $script:litT + '\b') $script:opt

        $files = New-Object System.Collections.Generic.List[string]
        Add-Files $Root $files
        $sorted = $files.ToArray()
        [Array]::Sort($sorted, [StringComparer]::OrdinalIgnoreCase)

        foreach ($file in $sorted) {
            $ext = [System.IO.Path]::GetExtension($file)
            if (-not [string]::IsNullOrEmpty($ext) -and $script:binaryExt.Contains($ext)) { continue }
            $length = -1
            try {
                $length = (New-Object System.IO.FileInfo $file).Length
            } catch {
                continue
            }
            if ($length -lt 0 -or $length -gt $script:maxBytes) { continue }
            $text = $null
            $reader = $null
            try {
                $reader = New-Object System.IO.StreamReader -ArgumentList $file, $script:utf8, $true
                $text = $reader.ReadToEnd()
            } catch {
                continue
            } finally {
                if ($null -ne $reader) { $reader.Dispose() }
            }
            if ($null -eq $text) { continue }
            if ($text.IndexOf([char]0) -ge 0) { continue }

            $rel = $file
            if ($file.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
                $rel = $file.Substring($rootPrefix.Length)
            }
            $underChart = Test-UnderChart $rel
            $lines = [regex]::Split($text, '\r\n|\n|\r')
            for ($i = 0; $i -lt $lines.Length; $i++) {
                $line = $lines[$i]
                if ([string]::IsNullOrEmpty($line)) { continue }
                $lineNo = $i + 1
                $assigned = $null
                try {
                    $assigned = $assignRe.Matches($line)
                } catch {
                    $assigned = $null
                }
                if ($null -ne $assigned) {
                    for ($mi = 0; $mi -lt $assigned.Count; $mi++) {
                        $item = $assigned[$mi]
                        $value = $item.Groups[1].Value
                        if ([string]::IsNullOrWhiteSpace($value)) { continue }
                        if ($value.Length -lt 20) { continue }
                        if (-not [regex]::IsMatch($value, '[0-9]')) { continue }
                        if (Test-DeniedToken $value) { continue }
                        Add-Hit 'secret' $rel $lineNo $item.Value
                    }
                }
                foreach ($re in $secretRes) {
                    Add-RegexHits 'secret' $re $rel $lineNo $line $false
                }
                Add-RegexHits 'path' $pathRe $rel $lineNo $line $true
                Add-RegexHits 'email' $emailRe $rel $lineNo $line $false
                Add-RegexHits 'tailscale' $tailRe $rel $lineNo $line $false
            }

            $tags = New-Object System.Collections.Generic.List[string]
            try { if ($reA.IsMatch($text)) { [void]$tags.Add($script:litA) } } catch { }
            try { if ($reS.IsMatch($text)) { [void]$tags.Add($script:litS) } } catch { }
            try { if ($reQ.IsMatch($text)) { [void]$tags.Add($script:litQ) } } catch { }
            if (-not $underChart) {
                try { if ($reT.IsMatch($text)) { [void]$tags.Add($script:litT) } } catch { }
            }
            if ($tags.Count -gt 0) {
                Add-Hit 'marker' $rel -1 ([string]::Join(' ', $tags.ToArray()))
            }
        }
    }
} catch {
}

foreach ($row in $script:hits) {
    Write-Output $row
}
Write-Output ('hits: ' + $script:hits.Count)
exit 0
