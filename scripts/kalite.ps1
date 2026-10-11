<#
  Kalite kapısı: gizli bilgi taraması, açıklı paket denetimi, derleme, testler ve arayüz denetimi.
  Yalnızca sorunları yazdırır; hepsi geçerse çıkış kodu 0, biri kalırsa 1 olur.

  Kullanım:  powershell -File scripts\kalite.ps1
#>

$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$kok = Split-Path -Parent $PSScriptRoot
$cozum = Join-Path $kok 'DocMasterPro\DocMasterPro.sln'
$arayuzDenetimi = Join-Path $kok 'DocMasterPro\tools\UiCheck\UiCheck.csproj'
$sorunlar = New-Object System.Collections.Generic.List[string]

function Adim([string]$ad, [string[]]$hatalar) {
    if (-not $hatalar -or $hatalar.Count -eq 0) {
        Write-Host "GEÇTİ  $ad"
        return
    }

    Write-Host "KALDI  $ad"
    foreach ($hata in $hatalar) {
        Write-Host "       $hata"
        $sorunlar.Add("${ad}: $hata")
    }
}

function Satirlar([string]$metin) { return @($metin -split "`r?`n") }

# ---------- 1. Gizli bilgi ----------
$desen = 'ghp_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|gho_[A-Za-z0-9]{20,}|AKIA[0-9A-Z]{16}|AIza[0-9A-Za-z_-]{30,}|xox[baprs]-[A-Za-z0-9-]{10,}|-----BEGIN [A-Z ]*PRIVATE KEY-----'
$hatalar = @()
foreach ($dosya in @(git -C $kok grep -nIlE $desen -- . 2>$null)) {
    if ($dosya) { $hatalar += "gizli bilgi kalıbı: $dosya" }
}
foreach ($dosya in @(git -C $kok ls-files)) {
    if ($dosya -match '(^|/)\.env($|\.)|\.pfx$|\.p12$|\.pem$|\.snk$' -and $dosya -notmatch '\.env\.example$') {
        $hatalar += "depoda olmamalı: $dosya"
    }
}
if (@(git -C $kok remote -v | Where-Object { $_ -match '://[^/@\s]+@' }).Count -gt 0) {
    $hatalar += 'uzak depo adresinde kimlik bilgisi var'
}
Adim 'Gizli bilgi taraması' $hatalar

# ---------- 2. Açıklı paketler ----------
# İstisna yalnızca aşağıdaki paket sürümü ve kayıt numaraları için geçerlidir; yenisi çıkarsa denetim yine kalır.
# SixLabors.ImageSharp 3.2.0: düzeltmeler bu sürüme taşındı (SixLabors/ImageSharp#3208),
# ancak kayıtlardaki sürüm aralığı (<= 4.1.1) henüz güncellenmediği için NuGet hâlâ uyarıyor.
$istisnalar = @{
    'SixLabors.ImageSharp@3.2.0' = @(
        'GHSA-gwg2-r3hj-4w44', 'GHSA-j3p4-wp97-rph4', 'GHSA-j9gm-c75j-xc9q', 'GHSA-wmxv-xphr-5c9g', 'GHSA-jjfr-hcj7-qf5w')
}
$hatalar = @()
$json = dotnet list $cozum package --vulnerable --include-transitive --format json | Out-String
$rapor = $null
try { $rapor = $json | ConvertFrom-Json } catch { $hatalar += 'paket denetimi çalıştırılamadı' }
if ($rapor) {
    $bulunan = @{}
    foreach ($proje in $rapor.projects) {
        foreach ($cerceve in $proje.frameworks) {
            foreach ($paket in (@($cerceve.topLevelPackages) + @($cerceve.transitivePackages))) {
                if (-not $paket) { continue }
                $anahtar = "$($paket.id)@$($paket.resolvedVersion)"
                foreach ($acik in $paket.vulnerabilities) {
                    $kimlik = ($acik.advisoryurl -split '/')[-1]
                    if ($istisnalar.ContainsKey($anahtar) -and $istisnalar[$anahtar] -contains $kimlik) { continue }
                    $bulunan["$anahtar  $($acik.severity)  $kimlik"] = $true
                }
            }
        }
    }
    $hatalar += @($bulunan.Keys | Sort-Object)
}
Adim 'Açıklı paket denetimi' $hatalar

# ---------- 3. Derleme ----------
$cikti = dotnet build $cozum -c Debug -nologo -v q | Out-String
$derlendi = $LASTEXITCODE -eq 0
$hatalar = @()
if (-not $derlendi) {
    $hatalar += @(Satirlar $cikti | Where-Object { $_ -match ': error ' } |
        ForEach-Object { $_ -replace '\s*\[[^\]]*\]\s*$', '' } | Select-Object -Unique -First 15)
    if ($hatalar.Count -eq 0) { $hatalar += 'derleme başarısız' }
}
Adim 'Derleme' $hatalar

# ---------- 4. Testler ----------
if ($derlendi) {
    $cikti = dotnet test $cozum -c Debug --no-build --nologo -v q 2>&1 | ForEach-Object { "$_" } | Out-String
    $hatalar = @()
    if ($LASTEXITCODE -ne 0) {
        $hatalar += @(Satirlar $cikti | Where-Object { $_ -match '\[FAIL\]' } |
            ForEach-Object { ($_ -replace '^\[xUnit\.net[^\]]*\]\s*', '').Trim() } | Select-Object -Unique -First 20)
        if ($hatalar.Count -eq 0) { $hatalar += 'testler başarısız' }
    }
    $ozet = Satirlar $cikti | Where-Object { $_ -match 'Toplam:|Total:' } | Select-Object -Last 1
    if ($ozet -match '(Toplam|Total):\s*(\d+)') { Adim "Testler ($($Matches[2]) test)" $hatalar } else { Adim 'Testler' $hatalar }
}
else {
    Adim 'Testler' @('derleme başarısız olduğu için çalıştırılmadı')
}

# ---------- 5. Arayüz: yerleşim, kontrast, klavye ----------
if ($derlendi) {
    $cikti = dotnet run --project $arayuzDenetimi -c Debug | Out-String
    $hatalar = @()
    if ($LASTEXITCODE -ne 0) {
        $hatalar += @(Satirlar $cikti | Where-Object { $_ -match '^SORUN ' } | ForEach-Object { $_ -replace '^SORUN ', '' })
        if ($hatalar.Count -eq 0) { $hatalar += 'arayüz denetimi çalıştırılamadı' }
    }
    Adim 'Arayüz denetimi (yerleşim, kontrast, klavye)' $hatalar
}
else {
    Adim 'Arayüz denetimi' @('derleme başarısız olduğu için çalıştırılmadı')
}

if ($sorunlar.Count -eq 0) {
    Write-Host 'Kalite kapısı: GEÇTİ'
    exit 0
}

Write-Host "Kalite kapısı: KALDI ($($sorunlar.Count) sorun)"
exit 1
