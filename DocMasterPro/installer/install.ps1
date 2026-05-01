param(
    [switch]$Elevated,
    [string]$BootstrapRoot
)

$ErrorActionPreference = 'Stop'

$appName = 'DocMaster Pro'
$installDir = Join-Path $env:ProgramFiles $appName
$payloadName = 'DocMasterProPayload.zip'
$progId = 'DocMasterPro.PDF'
$registeredApplicationName = 'DocMaster Pro'
$capabilitiesRelativePath = 'Software\DocMasterPro\Capabilities'

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Show-SetupMessage {
    param(
        [string]$Message,
        [string]$Title = 'DocMaster Pro Setup'
    )

    Add-Type -AssemblyName System.Windows.Forms
    [System.Windows.Forms.MessageBox]::Show($Message, $Title, 'OK', 'Information') | Out-Null
}

function New-Shortcut {
    param(
        [string]$Path,
        [string]$TargetPath,
        [string]$WorkingDirectory,
        [string]$IconLocation
    )

    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($Path)
    $shortcut.TargetPath = $TargetPath
    $shortcut.WorkingDirectory = $WorkingDirectory
    $shortcut.IconLocation = $IconLocation
    $shortcut.Save()
}

function Register-PdfAssociation {
    param(
        [string]$ExePath
    )

    $classesRoot = 'HKLM:\Software\Classes'
    $progIdPath = Join-Path $classesRoot $progId
    $defaultIconPath = Join-Path $progIdPath 'DefaultIcon'
    $commandPath = Join-Path $progIdPath 'shell\open\command'
    $openWithProgIdsPath = Join-Path $classesRoot '.pdf\OpenWithProgids'
    $openWithListPath = Join-Path $classesRoot '.pdf\OpenWithList\DocConverter.exe'
    $applicationPath = Join-Path $classesRoot 'Applications\DocConverter.exe'
    $applicationCommandPath = Join-Path $applicationPath 'shell\open\command'
    $applicationSupportedTypesPath = Join-Path $applicationPath 'SupportedTypes'
    $capabilitiesPath = 'HKLM:\Software\DocMasterPro\Capabilities'
    $fileAssociationsPath = Join-Path $capabilitiesPath 'FileAssociations'
    $registeredApplicationsPath = 'HKLM:\Software\RegisteredApplications'
    $openCommand = '"' + $ExePath + '" "%1"'

    New-Item -Path $progIdPath -Force | Out-Null
    Set-Item -Path $progIdPath -Value 'DocMaster Pro PDF Document'
    New-Item -Path $defaultIconPath -Force | Out-Null
    Set-Item -Path $defaultIconPath -Value "$ExePath,0"
    New-Item -Path $commandPath -Force | Out-Null
    Set-Item -Path $commandPath -Value $openCommand

    New-Item -Path $openWithProgIdsPath -Force | Out-Null
    New-ItemProperty -Path $openWithProgIdsPath -Name $progId -Value ([byte[]]@()) -PropertyType Binary -Force | Out-Null
    New-Item -Path $openWithListPath -Force | Out-Null

    New-Item -Path $applicationPath -Force | Out-Null
    New-ItemProperty -Path $applicationPath -Name FriendlyAppName -Value $appName -PropertyType String -Force | Out-Null
    New-Item -Path $applicationCommandPath -Force | Out-Null
    Set-Item -Path $applicationCommandPath -Value $openCommand
    New-Item -Path $applicationSupportedTypesPath -Force | Out-Null
    New-ItemProperty -Path $applicationSupportedTypesPath -Name '.pdf' -Value '' -PropertyType String -Force | Out-Null

    New-Item -Path $capabilitiesPath -Force | Out-Null
    New-Item -Path $fileAssociationsPath -Force | Out-Null
    New-ItemProperty -Path $capabilitiesPath -Name ApplicationName -Value $appName -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $capabilitiesPath -Name ApplicationDescription -Value 'Open PDF files with DocMaster Pro PDF Studio.' -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $capabilitiesPath -Name ApplicationIcon -Value "$ExePath,0" -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $fileAssociationsPath -Name '.pdf' -Value $progId -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $registeredApplicationsPath -Name $registeredApplicationName -Value $capabilitiesRelativePath -PropertyType String -Force | Out-Null
}

if (-not (Test-IsAdministrator)) {
    $bootstrap = Join-Path $env:TEMP ('DocMasterProSetup_' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $bootstrap -Force | Out-Null

    Copy-Item -LiteralPath $PSCommandPath -Destination (Join-Path $bootstrap 'install.ps1') -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $payloadName) -Destination (Join-Path $bootstrap $payloadName) -Force

    $arguments = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', "`"$(Join-Path $bootstrap 'install.ps1')`"",
        '-Elevated',
        '-BootstrapRoot', "`"$bootstrap`""
    ) -join ' '

    Start-Process -FilePath 'powershell.exe' -ArgumentList $arguments -Verb RunAs
    exit
}

$payloadPath = Join-Path $PSScriptRoot $payloadName
if ($BootstrapRoot) {
    $payloadPath = Join-Path $BootstrapRoot $payloadName
}

if (-not (Test-Path -LiteralPath $payloadPath)) {
    throw "Payload not found: $payloadPath"
}

$extractDir = Join-Path $env:TEMP ('DocMasterProPayload_' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $extractDir -Force | Out-Null

try {
    Expand-Archive -LiteralPath $payloadPath -DestinationPath $extractDir -Force

    New-Item -ItemType Directory -Path $installDir -Force | Out-Null
    Copy-Item -Path (Join-Path $extractDir '*') -Destination $installDir -Recurse -Force

    $exePath = Join-Path $installDir 'DocConverter.exe'
    if (-not (Test-Path -LiteralPath $exePath)) {
        throw "Application executable not found after install: $exePath"
    }

    Register-PdfAssociation -ExePath $exePath

    $uninstallPath = Join-Path $installDir 'uninstall.ps1'
    $uninstallScript = @"
param([switch]`$Elevated)

`$ErrorActionPreference = 'SilentlyContinue'
`$appName = '$appName'
`$installDir = '$installDir'
`$progId = '$progId'
`$registeredApplicationName = '$registeredApplicationName'

function Test-IsAdministrator {
    `$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    `$principal = [Security.Principal.WindowsPrincipal]::new(`$identity)
    return `$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-IsAdministrator)) {
    `$arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + `$PSCommandPath + '" -Elevated'
    Start-Process -FilePath 'powershell.exe' -ArgumentList `$arguments -Verb RunAs
    exit
}

Remove-Item -LiteralPath (Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) (`$appName + '.lnk')) -Force
Remove-Item -LiteralPath (Join-Path `$env:ProgramData 'Microsoft\Windows\Start Menu\Programs\DocMaster Pro') -Recurse -Force
Remove-Item -LiteralPath 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\DocMasterPro' -Recurse -Force
Remove-ItemProperty -LiteralPath 'HKLM:\Software\Classes\.pdf\OpenWithProgids' -Name `$progId -Force
Remove-Item -LiteralPath 'HKLM:\Software\Classes\.pdf\OpenWithList\DocConverter.exe' -Recurse -Force
Remove-ItemProperty -LiteralPath 'HKLM:\Software\RegisteredApplications' -Name `$registeredApplicationName -Force
Remove-Item -LiteralPath 'HKLM:\Software\Classes\DocMasterPro.PDF' -Recurse -Force
Remove-Item -LiteralPath 'HKLM:\Software\Classes\Applications\DocConverter.exe' -Recurse -Force
Remove-Item -LiteralPath 'HKLM:\Software\DocMasterPro' -Recurse -Force
Remove-Item -LiteralPath `$installDir -Recurse -Force
"@
    Set-Content -LiteralPath $uninstallPath -Value $uninstallScript -Encoding UTF8 -Force

    $desktopShortcut = Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) "$appName.lnk"
    New-Shortcut -Path $desktopShortcut -TargetPath $exePath -WorkingDirectory $installDir -IconLocation "$exePath,0"

    $startMenuDir = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\DocMaster Pro'
    New-Item -ItemType Directory -Path $startMenuDir -Force | Out-Null
    New-Shortcut -Path (Join-Path $startMenuDir "$appName.lnk") -TargetPath $exePath -WorkingDirectory $installDir -IconLocation "$exePath,0"
    New-Shortcut -Path (Join-Path $startMenuDir 'Uninstall DocMaster Pro.lnk') -TargetPath 'powershell.exe' -WorkingDirectory $installDir -IconLocation "$exePath,0"
    $uninstallShortcut = Join-Path $startMenuDir 'Uninstall DocMaster Pro.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($uninstallShortcut)
    $shortcut.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$uninstallPath`""
    $shortcut.Save()

    $registryPath = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\DocMasterPro'
    New-Item -Path $registryPath -Force | Out-Null
    New-ItemProperty -Path $registryPath -Name DisplayName -Value $appName -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $registryPath -Name DisplayVersion -Value '1.0.6' -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $registryPath -Name Publisher -Value 'DocMaster Pro' -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $registryPath -Name InstallLocation -Value $installDir -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $registryPath -Name DisplayIcon -Value $exePath -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $registryPath -Name UninstallString -Value "`"powershell.exe`" -NoProfile -ExecutionPolicy Bypass -File `"$uninstallPath`"" -PropertyType String -Force | Out-Null

    Show-SetupMessage "$appName installed successfully."
}
finally {
    Remove-Item -LiteralPath $extractDir -Recurse -Force -ErrorAction SilentlyContinue
    if ($BootstrapRoot) {
        Remove-Item -LiteralPath $BootstrapRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
