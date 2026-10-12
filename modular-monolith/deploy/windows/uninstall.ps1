#Requires -Version 5.1
param(
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'ModularMonolith'),
    [string]$DataDir = (Join-Path $env:ProgramData 'ModularMonolith'),
    [switch]$RemoveData
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ServiceName = 'ModularMonolith'

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

if (-not (Test-IsAdministrator)) {
    [Console]::Error.WriteLine('Run this uninstaller as Administrator.')
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

if (Test-Path -LiteralPath $InstallDir) {
    Remove-Item -LiteralPath $InstallDir -Recurse -Force
}

if ($RemoveData -and (Test-Path -LiteralPath $DataDir)) {
    Remove-Item -LiteralPath $DataDir -Recurse -Force
}
