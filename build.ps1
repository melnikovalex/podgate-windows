<#
.SYNOPSIS
  Builds the installer: artifacts\setup\PodGate-Setup-<VERSION>.exe

.DESCRIPTION
  Publishes the service and the app self-contained into one folder (they share the runtime files, so the
  runtime ships once), then compiles setup\PodGate.iss with Inno Setup. VERSION is the only place the
  version lives. Needs the .NET 10 SDK and Inno Setup 6 (winget install JRSoftware.InnoSetup).
#>
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$version = (Get-Content (Join-Path $root 'VERSION') -Raw).Trim()
$publish = Join-Path $root 'artifacts\publish\PodGate'

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

foreach ($project in 'src\PodGate.Service\PodGate.Service.csproj', 'src\PodGate.App\PodGate.App.csproj') {
    & dotnet publish (Join-Path $root $project) -c Release -p:PublishSingleFile=false -o $publish --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed: $project" }
}

$iscc = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe')
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found. Install it: winget install JRSoftware.InnoSetup' }

& $iscc /Q "/DAppVersion=$version" (Join-Path $root 'setup\PodGate.iss')
if ($LASTEXITCODE -ne 0) { throw 'ISCC failed' }

Write-Host "Built artifacts\setup\PodGate-Setup-$version.exe"
