#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackageDirectory,

    [Parameter(Mandatory = $true)]
    [ValidateSet('FrameworkDependent', 'SelfContained')]
    [string]$PublicationMode
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-File {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required package file '$Path' does not exist."
    }
}

function Assert-Executable {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    Assert-File -Path $Path

    if (-not $IsWindows) {
        $mode = [System.IO.File]::GetUnixFileMode($Path)
        if (($mode -band [System.IO.UnixFileMode]::UserExecute) -eq 0) {
            throw "Required package executable '$Path' is not executable by its owner."
        }
    }
}

$packagePath = (Resolve-Path -LiteralPath $PackageDirectory).Path
$projectRoot = Split-Path -Parent $PSScriptRoot
$hostProjectPath = Join-Path $projectRoot 'src/ModularMonolith.Host/ModularMonolith.Host.csproj'

[xml]$hostProject = Get-Content -LiteralPath $hostProjectPath -Raw
$workerReferences = @(
    $hostProject.Project.ItemGroup.ProjectReference |
        Where-Object { [string]$_.PackageAsWorker -eq 'true' }
)

if ($workerReferences.Count -eq 0) {
    throw "The Host project does not define any worker references."
}

$expectedWorkers = @(
    $workerReferences |
        ForEach-Object {
            $reference = ([string]$_.Include).Replace('\', '/')
            [System.IO.Path]::GetFileNameWithoutExtension($reference)
        } |
        Sort-Object
)
$workersDirectory = Join-Path $packagePath 'workers'

if (-not (Test-Path -LiteralPath $workersDirectory -PathType Container)) {
    throw "The package does not contain a workers directory."
}

$actualWorkers = @(
    Get-ChildItem -LiteralPath $workersDirectory -Directory |
        Select-Object -ExpandProperty Name |
        Sort-Object
)
$workerDifference = Compare-Object -ReferenceObject $expectedWorkers -DifferenceObject $actualWorkers
if ($workerDifference) {
    $expectedText = $expectedWorkers -join ', '
    $actualText = $actualWorkers -join ', '
    throw "Worker package mismatch. Expected: [$expectedText]. Actual: [$actualText]."
}

$hostExecutableName = if ($IsWindows) { 'ModularMonolith.Host.exe' } else { 'ModularMonolith.Host' }
$hostExecutable = Join-Path $packagePath $hostExecutableName
Assert-Executable -Path $hostExecutable
Assert-File -Path (Join-Path $packagePath 'ModularMonolith.Host.dll')
Assert-File -Path (Join-Path $packagePath 'ModularMonolith.Host.deps.json')
Assert-File -Path (Join-Path $packagePath 'ModularMonolith.Host.runtimeconfig.json')

foreach ($configuration in @('appsettings.json', 'appsettings.linux.json', 'appsettings.windows.json')) {
    Assert-File -Path (Join-Path $packagePath $configuration)
}

foreach ($worker in $expectedWorkers) {
    $workerDirectory = Join-Path $workersDirectory $worker
    $workerExecutableName = if ($IsWindows) { "$worker.exe" } else { $worker }

    Assert-Executable -Path (Join-Path $workerDirectory $workerExecutableName)
    Assert-File -Path (Join-Path $workerDirectory "$worker.dll")
    Assert-File -Path (Join-Path $workerDirectory "$worker.deps.json")
    Assert-File -Path (Join-Path $workerDirectory "$worker.runtimeconfig.json")

    $rootWorkerFiles = @(Get-ChildItem -LiteralPath $packagePath -File -Filter "$worker*")
    if ($rootWorkerFiles.Count -ne 0) {
        throw "Worker '$worker' has unexpected files at the package root."
    }
}

if ($PublicationMode -eq 'SelfContained') {
    $runtimeHostName = if ($IsWindows) { 'hostfxr.dll' } else { 'libhostfxr.so' }
    Assert-File -Path (Join-Path $packagePath $runtimeHostName)

    foreach ($worker in $expectedWorkers) {
        Assert-File -Path (Join-Path (Join-Path $workersDirectory $worker) $runtimeHostName)
    }
}

if ($IsWindows) {
    foreach ($script in @('install.ps1', 'run.ps1', 'uninstall.ps1')) {
        Assert-File -Path (Join-Path $packagePath "scripts/$script")
    }
}
else {
    foreach ($script in @('install.sh', 'run.sh', 'uninstall.sh')) {
        Assert-File -Path (Join-Path $packagePath "scripts/$script")
    }

    $servicePath = Join-Path $packagePath 'systemd/modular-monolith.service'
    Assert-File -Path $servicePath
    $serviceDefinition = Get-Content -LiteralPath $servicePath -Raw
    if (-not $serviceDefinition.Contains('ExecStart=/opt/modular-monolith/ModularMonolith.Host')) {
        throw "The systemd service does not launch the packaged Host apphost."
    }
}

Write-Host "Verified $PublicationMode package at '$packagePath' with $($expectedWorkers.Count) workers."
