<#
    Çeviri denetimi: koddaki tüm dil anahtarlarının her lang\*.json dosyasında bulunduğunu,
    dosyalarda kullanılmayan anahtar olmadığını ve {0} yer tutucularının dillerde eşleştiğini kontrol eder.
    Kullanım:  .\tools\Test-Translations.ps1      (sorun yoksa 0, varsa 1 ile çıkar)
#>
$root = Split-Path $PSScriptRoot -Parent
$langDir = Join-Path $root 'lang'

# Kodda geçen anahtarlar: T 'anahtar' (PowerShell) ve Loc.T("anahtar") / "flag.x" gibi dizeler (C#)
$keyPattern = '[a-z]+(?:\.[a-z]+)+'
$used = New-Object 'System.Collections.Generic.HashSet[string]'
$ps = [IO.File]::ReadAllText((Join-Path $root 'NetGadget.ps1'), [Text.Encoding]::UTF8)
foreach ($m in [regex]::Matches($ps, "\bT\s+['""]($keyPattern)['""]")) { [void]$used.Add($m.Groups[1].Value) }
foreach ($cs in 'NetSampler.cs', 'Inspector.cs') {
    $src = [IO.File]::ReadAllText((Join-Path $root $cs), [Text.Encoding]::UTF8)
    foreach ($m in [regex]::Matches($src, 'Loc\.T\(([^;]*)')) {
        foreach ($k in [regex]::Matches($m.Groups[1].Value, """($keyPattern)""")) { [void]$used.Add($k.Groups[1].Value) }
    }
}
# Çalışma anında üretilen anahtarlar
foreach ($s in 'rate', 'session', 'connections', 'name', 'risk') { [void]$used.Add("sort.$s.label"); [void]$used.Add("sort.$s.head") }
foreach ($k in 'language.name', 'language.culture') { [void]$used.Add($k) }

$problems = 0
$langs = @{}
foreach ($f in Get-ChildItem $langDir -Filter '*.json') {
    $obj = [IO.File]::ReadAllText($f.FullName, [Text.Encoding]::UTF8) | ConvertFrom-Json
    $d = @{}
    foreach ($p in $obj.PSObject.Properties) { $d[$p.Name] = [string]$p.Value }
    $langs[$f.BaseName] = $d
}

foreach ($code in $langs.Keys | Sort-Object) {
    $d = $langs[$code]
    $missing = @($used | Where-Object { -not $d.ContainsKey($_) } | Sort-Object)
    $unused = @($d.Keys | Where-Object { -not $used.Contains($_) } | Sort-Object)
    "[$code] $($d.Count) anahtar · eksik: $($missing.Count) · kullanılmayan: $($unused.Count)"
    $missing | ForEach-Object { "    EKSİK: $_"; $problems++ }
    $unused | ForEach-Object { "    KULLANILMIYOR: $_"; $problems++ }
}

# Yer tutucular ({0}, {1}) İngilizce ile aynı olmalı
$en = $langs['en']
foreach ($code in $langs.Keys | Where-Object { $_ -ne 'en' }) {
    foreach ($k in $langs[$code].Keys) {
        if (-not $en.ContainsKey($k)) { continue }
        $a = (@([regex]::Matches($en[$k], '\{\d+\}') | ForEach-Object { $_.Value } | Sort-Object -Unique)) -join ','
        $b = (@([regex]::Matches($langs[$code][$k], '\{\d+\}') | ForEach-Object { $_.Value } | Sort-Object -Unique)) -join ','
        if ($a -ne $b) { "    [$code] YER TUTUCU FARKI: $k (en: $a / $code`: $b)"; $problems++ }
    }
}

"kodda kullanılan anahtar: $($used.Count) · sorun: $problems"
if ($problems -gt 0) { exit 1 }
