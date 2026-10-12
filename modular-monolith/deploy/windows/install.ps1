#Requires -Version 5.1
param(
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'ModularMonolith'),
    [string]$DataDir = (Join-Path $env:ProgramData 'ModularMonolith')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ServiceName = 'ModularMonolith'
$ServiceDisplayName = 'Modular Monolith Host'
$ServiceDescription = 'Hosts the Modular Monolith application and worker processes.'
$ServiceAccount = 'NT SERVICE\ModularMonolith'
$PackageDir = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$ExecutablePath = Join-Path $InstallDir 'ModularMonolith.Host.exe'
$ServiceRegistryPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
$ImageCacheDatabasePath = Join-Path $DataDir 'image-cache.db'

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Invoke-Native {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [string[]]$ArgumentList = @(),

        [int[]]$SuccessExitCodes = @(0)
    )

    & $FilePath @ArgumentList
    $exitCode = $LASTEXITCODE

    if ($SuccessExitCodes -notcontains $exitCode) {
        throw "Command '$FilePath $($ArgumentList -join ' ')' failed with exit code $exitCode."
    }

    return $exitCode
}

function Get-ServiceInstallState {
    & sc.exe query $ServiceName 2>$null | Out-Null
    $exitCode = $LASTEXITCODE

    switch ($exitCode) {
        0 { return 'Present' }
        1060 { return 'Absent' }
        1072 { return 'Deleting' }
        default { throw "Command 'sc.exe query $ServiceName' failed with exit code $exitCode." }
    }
}

function Wait-ServiceRemoved {
    param(
        [int]$TimeoutSeconds = 30
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if ((Get-ServiceInstallState) -eq 'Absent') {
            return
        }

        Start-Sleep -Milliseconds 500
    }

    throw "Timed out waiting for service '$ServiceName' to be removed."
}

function Wait-ServiceStatus {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [System.ServiceProcess.ServiceControllerStatus]$Status,

        [int]$TimeoutSeconds = 30
    )

    $service = Get-Service -Name $Name -ErrorAction Stop
    $service.Refresh()

    if ($service.Status -ne $Status) {
        $service.WaitForStatus($Status, [TimeSpan]::FromSeconds($TimeoutSeconds))
        $service.Refresh()
    }

    return $service
}

function Clear-DirectoryContents {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    foreach ($item in Get-ChildItem -LiteralPath $Path -Force) {
        Remove-Item -LiteralPath $item.FullName -Recurse -Force
    }
}

function Show-ServiceStatus {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    Get-Service -Name $Name |
        Select-Object Status, Name, DisplayName |
        Format-Table -AutoSize
}

if (-not (Test-IsAdministrator)) {
    [Console]::Error.WriteLine('Run this installer as Administrator.')
    exit 1
}

$serviceState = Get-ServiceInstallState
if ($serviceState -eq 'Deleting') {
    Wait-ServiceRemoved -TimeoutSeconds 30
}
elseif ($serviceState -eq 'Present') {
    $existingService = Get-Service -Name $ServiceName -ErrorAction Stop
    if ($existingService.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
        if ($existingService.Status -ne [System.ServiceProcess.ServiceControllerStatus]::StopPending) {
            Stop-Service -Name $ServiceName
        }

        Wait-ServiceStatus -Name $ServiceName -Status ([System.ServiceProcess.ServiceControllerStatus]::Stopped) -TimeoutSeconds 30 | Out-Null
    }

    Invoke-Native -FilePath 'sc.exe' -ArgumentList @('delete', $ServiceName) | Out-Null
    Wait-ServiceRemoved -TimeoutSeconds 30
}

New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
Clear-DirectoryContents -Path $InstallDir

$scriptsDirectory = Join-Path $PackageDir 'scripts'
Invoke-Native -FilePath 'robocopy.exe' -ArgumentList @(
    $PackageDir,
    $InstallDir,
    '/E',
    '/R:2',
    '/W:1',
    '/XD',
    $scriptsDirectory
) -SuccessExitCodes @(0, 1, 2, 3, 4, 5, 6, 7) | Out-Null

New-Item -ItemType Directory -Path $DataDir -Force | Out-Null

Invoke-Native -FilePath 'sc.exe' -ArgumentList @(
    'create',
    $ServiceName,
    'binPath=',
    "`"$ExecutablePath`"",
    'start=',
    'auto',
    'obj=',
    $ServiceAccount,
    'DisplayName=',
    $ServiceDisplayName
) | Out-Null

Invoke-Native -FilePath 'sc.exe' -ArgumentList @('sidtype', $ServiceName, 'unrestricted') | Out-Null
Invoke-Native -FilePath 'sc.exe' -ArgumentList @('description', $ServiceName, $ServiceDescription) | Out-Null
# Windows has no equivalent of systemd StartLimitBurst.
Invoke-Native -FilePath 'sc.exe' -ArgumentList @('failure', $ServiceName, 'reset=', '60', 'actions=', 'restart/2000/restart/2000/restart/2000') | Out-Null
Invoke-Native -FilePath 'sc.exe' -ArgumentList @('failureflag', $ServiceName, '1') | Out-Null

Invoke-Native -FilePath 'icacls.exe' -ArgumentList @(
    $InstallDir,
    '/grant',
    "${ServiceAccount}:(OI)(CI)RX",
    '/T'
) | Out-Null

Invoke-Native -FilePath 'icacls.exe' -ArgumentList @(
    $DataDir,
    '/grant',
    "${ServiceAccount}:(OI)(CI)M",
    '/T'
) | Out-Null

New-ItemProperty -Path $ServiceRegistryPath -Name 'Environment' -PropertyType MultiString -Value @(
    'ASPNETCORE_URLS=http://127.0.0.1:5227',
    "ImageCache__DatabasePath=$ImageCacheDatabasePath"
) -Force | Out-Null

Start-Service -Name $ServiceName
Wait-ServiceStatus -Name $ServiceName -Status ([System.ServiceProcess.ServiceControllerStatus]::Running) -TimeoutSeconds 30 | Out-Null
Show-ServiceStatus -Name $ServiceName
