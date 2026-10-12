#Requires -Version 5.1
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('start', 'stop')]
    [string]$Action
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ServiceName = 'ModularMonolith'
$ServiceTimeoutSeconds = 30

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
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
    [Console]::Error.WriteLine('Run this service controller as Administrator.')
    exit 1
}

if ((Get-ServiceInstallState) -ne 'Present') {
    [Console]::Error.WriteLine("$ServiceName is not installed. Run install.ps1 first.")
    exit 1
}

switch ($Action) {
    'start' {
        $service = Get-Service -Name $ServiceName -ErrorAction Stop
        if ($service.Status -eq [System.ServiceProcess.ServiceControllerStatus]::StartPending) {
            Wait-ServiceStatus -Name $ServiceName -Status ([System.ServiceProcess.ServiceControllerStatus]::Running) -TimeoutSeconds $ServiceTimeoutSeconds | Out-Null
        }
        elseif ($service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Running) {
            Start-Service -Name $ServiceName
            Wait-ServiceStatus -Name $ServiceName -Status ([System.ServiceProcess.ServiceControllerStatus]::Running) -TimeoutSeconds $ServiceTimeoutSeconds | Out-Null
        }
    }
    'stop' {
        $service = Get-Service -Name $ServiceName -ErrorAction Stop
        if ($service.Status -eq [System.ServiceProcess.ServiceControllerStatus]::StopPending) {
            Wait-ServiceStatus -Name $ServiceName -Status ([System.ServiceProcess.ServiceControllerStatus]::Stopped) -TimeoutSeconds $ServiceTimeoutSeconds | Out-Null
        }
        elseif ($service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
            Stop-Service -Name $ServiceName
            Wait-ServiceStatus -Name $ServiceName -Status ([System.ServiceProcess.ServiceControllerStatus]::Stopped) -TimeoutSeconds $ServiceTimeoutSeconds | Out-Null
        }
    }
}

Show-ServiceStatus -Name $ServiceName
