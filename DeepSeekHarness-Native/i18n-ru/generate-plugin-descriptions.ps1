param(
    [Parameter(Mandatory=$true)][string]$ZipPath,
    [Parameter(Mandatory=$true)][string]$OutputPath
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
$zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $ZipPath))
try {
    $items = @(
        foreach ($entry in $zip.Entries) {
            if ($entry.FullName -notmatch 'node_modules/@deepseek-ai/[^/]+/package\.json$') { continue }
            $reader = [IO.StreamReader]::new($entry.Open())
            try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
            if ($manifest.name -and $manifest.description) {
                [pscustomobject]@{ name = [string]$manifest.name; en = [string]$manifest.description }
            }
        }
    )
} finally { $zip.Dispose() }

$result = [ordered]@{}
for ($offset = 0; $offset -lt $items.Count; $offset += 8) {
    $batch = @($items[$offset..([Math]::Min($items.Count - 1, $offset + 7))])
    $query = ($batch | ForEach-Object { $_.en }) -join "`n"
    $uri = 'https://translate.googleapis.com/translate_a/single?client=gtx&sl=en&tl=ru&dt=t&q=' + [uri]::EscapeDataString($query)
    $response = Invoke-RestMethod -Uri $uri -TimeoutSec 45
    $translation = (($response[0] | ForEach-Object { $_[0] }) -join '') -replace "`r", ''
    $lines = @($translation.TrimEnd("`n").Split("`n"))
    if ($lines.Count -ne $batch.Count) {
        throw "Translation count mismatch at $offset`: expected $($batch.Count), got $($lines.Count)"
    }
    for ($i = 0; $i -lt $batch.Count; $i++) {
        $result[$batch[$i].name] = [ordered]@{ en = $batch[$i].en; ru = $lines[$i].Trim() }
    }
    Write-Host "Translated $($offset + $batch.Count)/$($items.Count)"
    Start-Sleep -Milliseconds 250
}

$result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $OutputPath -Encoding utf8
Write-Host "Wrote $($result.Count) plugin descriptions to $OutputPath"
