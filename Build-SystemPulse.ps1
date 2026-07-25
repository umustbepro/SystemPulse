[CmdletBinding()]
param(
    [ValidatePattern('^[^/\s]+/[^/\s]+$')]
    [string]$GitHubRepository = 'umustbepro/SystemPulse',

    [switch]$FrameworkDependent
)

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$project = Join-Path $projectRoot 'SystemPulse.csproj'
$publishFolder = Join-Path $projectRoot 'publish'
$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
$enableCompression = if ($FrameworkDependent) { 'false' } else { 'true' }

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET 10 SDK is required. Install it, then run this script again.'
}

$sdkVersion = & dotnet --version
if ($LASTEXITCODE -ne 0 -or [version]($sdkVersion.Split('-')[0]) -lt [version]'10.0.0') {
    throw "SystemPulse requires the .NET 10 SDK. Detected: $sdkVersion"
}

& dotnet restore $project --runtime win-x64
if ($LASTEXITCODE -ne 0) { throw 'Package restore failed.' }

& dotnet publish $project `
    --configuration Release `
    --runtime win-x64 `
    --self-contained $selfContained `
    --no-restore `
    --output $publishFolder `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:IncludeAllContentForSelfExtract=true `
    -p:EnableCompressionInSingleFile=$enableCompression `
    -p:PublishTrimmed=false `
    -p:DebugSymbols=false `
    -p:DebugType=None `
    -p:GitHubRepository=$GitHubRepository
if ($LASTEXITCODE -ne 0) { throw 'SystemPulse publish failed.' }

$executable = Join-Path $publishFolder 'SystemPulse.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw 'Publish completed without producing SystemPulse.exe.'
}

$unexpectedFiles = @(Get-ChildItem -LiteralPath $publishFolder -File | Where-Object Name -ne 'SystemPulse.exe')
if ($unexpectedFiles.Count -gt 0) {
    throw "The publish folder contains unexpected sidecar files: $($unexpectedFiles.Name -join ', ')"
}

$sizeMb = [math]::Round((Get-Item -LiteralPath $executable).Length / 1MB, 1)
Write-Host "Built $executable ($sizeMb MB)"
Write-Host "GitHub updater repository: $GitHubRepository"
if ($FrameworkDependent) {
    Write-Host 'This smaller build requires the .NET 10 Desktop Runtime on the user computer.'
} else {
    Write-Host 'This self-contained build requires no separate .NET installation.'
}
