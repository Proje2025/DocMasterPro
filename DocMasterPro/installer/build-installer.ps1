$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$projectPath = Join-Path $repoRoot 'desktop-app\DocConverter.csproj'
$artifactsRoot = Join-Path $repoRoot 'artifacts'
$publishDir = Join-Path $artifactsRoot 'publish\DocMasterPro-win-x64'
$installerDir = Join-Path $artifactsRoot 'installer'
$setupPath = Join-Path $installerDir 'DocMasterProSetup.exe'
$buildRoot = Join-Path $env:TEMP 'DocMasterProInstallerBuild'
$payloadZip = Join-Path $buildRoot 'DocMasterProPayload.zip'
$stagedInstallScript = Join-Path $buildRoot 'install.ps1'
$sedPath = Join-Path $buildRoot 'DocMasterProSetup.sed'
$stagedSetupPath = Join-Path $buildRoot 'DocMasterProSetup.exe'

function Assert-SafeChildPath {
    param(
        [string]$Parent,
        [string]$Child
    )

    $parentFull = [IO.Path]::GetFullPath($Parent).TrimEnd('\') + '\'
    $childFull = [IO.Path]::GetFullPath($Child)
    if (-not $childFull.StartsWith($parentFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe path outside expected parent: $Child"
    }
}

foreach ($path in @($publishDir, $installerDir)) {
    Assert-SafeChildPath -Parent $artifactsRoot -Child $path
}

if (Test-Path -LiteralPath $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}

if (Test-Path -LiteralPath $buildRoot) {
    Remove-Item -LiteralPath $buildRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
New-Item -ItemType Directory -Path $installerDir -Force | Out-Null
New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null

dotnet publish $projectPath `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -o $publishDir `
    -p:PublishSingleFile=false `
    -p:UseAppHost=true

Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $payloadZip -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'install.ps1') -Destination $stagedInstallScript -Force

$sed = @"
[Version]
Class=IEXPRESS
SEDVersion=3
[Options]
PackagePurpose=InstallApp
ShowInstallProgramWindow=1
HideExtractAnimation=0
UseLongFileName=1
InsideCompressed=0
CAB_FixedSize=0
CAB_ResvCodeSigning=0
RebootMode=N
InstallPrompt=DocMaster Pro setup will start.
DisplayLicense=
FinishMessage=DocMaster Pro setup completed.
TargetName=%TargetName%
FriendlyName=DocMaster Pro Setup
AppLaunched=%AppLaunched%
PostInstallCmd=<None>
AdminQuietInstCmd=
UserQuietInstCmd=
SourceFiles=SourceFiles
[SourceFiles]
SourceFiles0=%SourceFiles0%
[SourceFiles0]
%FILE0%=
%FILE1%=
[Strings]
TargetName=$stagedSetupPath
AppLaunched=powershell.exe -NoProfile -ExecutionPolicy Bypass -File install.ps1
SourceFiles0=$buildRoot
FILE0=install.ps1
FILE1=DocMasterProPayload.zip
"@

Set-Content -LiteralPath $sedPath -Value $sed -Encoding ASCII -Force

$iexpress = Join-Path $env:WINDIR 'System32\iexpress.exe'
if (-not (Test-Path -LiteralPath $iexpress)) {
    throw 'iexpress.exe was not found.'
}

$packageStart = Get-Date
& $iexpress /N /Q $sedPath

$deadline = (Get-Date).AddMinutes(8)
while (-not (Test-Path -LiteralPath $stagedSetupPath)) {
    if ((Get-Date) -gt $deadline) {
        break
    }

    Start-Sleep -Seconds 2
}

if (-not (Test-Path -LiteralPath $stagedSetupPath)) {
    throw "Setup executable was not created: $stagedSetupPath"
}

$previousLength = -1
for ($attempt = 0; $attempt -lt 10; $attempt++) {
    $currentLength = (Get-Item -LiteralPath $stagedSetupPath).Length
    if ($currentLength -eq $previousLength) {
        break
    }

    $previousLength = $currentLength
    Start-Sleep -Seconds 1
}

$copyDeadline = (Get-Date).AddMinutes(3)
$copied = $false
while (-not $copied) {
    try {
        Copy-Item -LiteralPath $stagedSetupPath -Destination $setupPath -Force
        $copied = $true
    }
    catch [System.IO.IOException] {
        if ((Get-Date) -gt $copyDeadline) {
            throw
        }

        Start-Sleep -Seconds 2
    }
}

Get-Process iexpress -ErrorAction SilentlyContinue |
    Where-Object { $_.StartTime -ge $packageStart.AddSeconds(-5) } |
    Stop-Process -Force -ErrorAction SilentlyContinue

Write-Host "Setup created: $setupPath"
