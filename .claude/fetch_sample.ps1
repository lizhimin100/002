param([int]$start=1, [int]$end=20)

$headers = @{"User-Agent"="Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36"}
$base = "https://www.ruwenxsw.com"
$catalogUrl = "https://www.ruwenxsw.com/dushu/112079305/"
$outDir = "d:\360安全浏览器下载\地下城\.claude\sample_book"
New-Item -ItemType Directory -Force $outDir | Out-Null

# First get the catalog to find real chapter URLs
Write-Host "Fetching catalog..."
$catalogResp = Invoke-WebRequest -Uri $catalogUrl -Headers $headers -TimeoutSec 15
$catalogHtml = $catalogResp.Content
$chapterLinks = [regex]::Matches($catalogHtml, '<a[^>]*href="(/dushu/112079305/\d+\.html)"[^>]*>\s*第(\d+)章')
Write-Host "Found $($chapterLinks.Count) chapter links in catalog"

# Build a map of chapter number -> URL
$chapterMap = @{}
foreach ($m in $chapterLinks) {
    $url = $m.Groups[1].Value
    $num = [int]$m.Groups[2].Value
    $chapterMap[$num] = $url
}

function Get-Chapter($url) {
    try {
        $resp = Invoke-WebRequest -Uri "$base$url" -Headers $headers -TimeoutSec 15
        $html = $resp.Content
        $match = [regex]::Match($html, '<div id="BookText">(.*?)</div>\s*<div class="link"', [System.Text.RegularExpressions.RegexOptions]::Singleline)
        if (-not $match.Success) {
            $match = [regex]::Match($html, '<div id="BookText">(.*?)</div>', [System.Text.RegularExpressions.RegexOptions]::Singleline)
        }
        if ($match.Success) {
            $text = $match.Groups[1].Value
            $text = $text -replace '<p>', "`n" -replace '</p>', "`n" -replace '<br\s*/?>', "`n"
            $text = $text -replace '<[^>]*>', ''
            $text = $text -replace '&nbsp;', ' ' -replace '&lt;', '<' -replace '&gt;', '>' -replace '&amp;', '&'
            $text = $text -replace '&quot;', '"' -replace '&ldquo;', '"' -replace '&rdquo;', '"'
            $text = $text -replace '&lsquo;', "'" -replace '&rsquo;', "'" -replace '&hellip;', '...'
            $lines = $text -split "`n" | ForEach-Object { $_.Trim() }
            $text = ($lines | Where-Object { $_.Length -gt 0 }) -join "`n"
            return $text.Trim()
        }
        return "CONTENT_NOT_FOUND"
    } catch {
        return "ERROR: $($_.Exception.Message)"
    }
}

for ($i = $start; $i -le $end; $i++) {
    Write-Host ""
    if (-not $chapterMap.ContainsKey($i)) {
        Write-Host "=== Chapter $i - NOT IN CATALOG ==="
        continue
    }
    $url = $chapterMap[$i]
    Write-Host "=== Chapter $i ($url) ==="
    $content = Get-Chapter $url
    $outFile = Join-Path $outDir "ch$i.txt"
    $content | Out-File -FilePath $outFile -Encoding utf8
    $preview = $content.Substring(0, [Math]::Min(200, $content.Length)) -replace "`n", " "
    Write-Host $preview
}
