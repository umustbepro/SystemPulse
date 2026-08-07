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

$mainXaml = Get-Content -LiteralPath (Join-Path $projectRoot 'MainWindow.xaml') -Raw
$projectText = Get-Content -LiteralPath $project -Raw
$bundledPackages = @(
    'librehardwaremonitorlib.0.9.7-pre715.nupkg',
    'diskinfotoolkit.2.1.2.nupkg',
    'blacksharp.core.1.1.3.nupkg',
    'ramspdtoolkit-ndd.1.5.0.nupkg',
    'system.io.ports.10.0.10.nupkg',
    'runtime.native.system.io.ports.10.0.10.nupkg'
)
$missingBundledPackages = @($bundledPackages | Where-Object { -not (Test-Path -LiteralPath (Join-Path $projectRoot $_) -PathType Leaf) })
$bundledSerialRuntimePackages = @(Get-ChildItem -LiteralPath $projectRoot -Filter 'runtime.*.runtime.native.System.IO.Ports.10.0.10.nupkg' -File)
if ($projectText -notmatch 'LibreHardwareMonitorLib" Version="0\.9\.7-pre715"' -or
    $missingBundledPackages.Count -gt 0 -or
    $bundledSerialRuntimePackages.Count -ne 16) {
    throw 'The newer bundled LibreHardwareMonitor fan backend is missing or the project does not reference it.'
}
$sharedLibreText = Get-Content -LiteralPath (Join-Path $projectRoot 'Services\SharedLibreHardware.cs') -Raw
if ($projectText -match 'ResolvedFileToPublish\s+Remove=' -or
    $projectText -match "Filename\)'\s*==\s*'(?:DiskInfoToolkit|RAMSPDToolkit-NDD|Mono\.Posix\.NETStandard)") {
    throw 'A LibreHardwareMonitor runtime dependency is being stripped. Publishing was stopped to prevent hardware discovery failures.'
}
$sensitiveBindings = [regex]::Matches($mainXaml, '<(?:Run|ProgressBar)\b[^>]*\{Binding[^>]*>')
$writableTelemetryBindings = @($sensitiveBindings | Where-Object { $_.Value -notmatch 'Mode\s*=\s*OneWay' })
if ($writableTelemetryBindings.Count -gt 0) {
    throw 'A read-only inline or progress telemetry binding is missing Mode=OneWay. Publishing was stopped to prevent a startup crash.'
}

Get-ChildItem -LiteralPath (Join-Path $projectRoot 'Controls') -Filter '*.xaml' | ForEach-Object {
    $controlXaml = Get-Content -LiteralPath $_.FullName -Raw
    $localKeys = @([regex]::Matches($controlXaml, 'x:Key\s*=\s*"([^"]+)"') | ForEach-Object { $_.Groups[1].Value })
    $staticKeys = @([regex]::Matches($controlXaml, '\{StaticResource\s+([^}\s]+)') | ForEach-Object { $_.Groups[1].Value })
    $missingKeys = @($staticKeys | Where-Object { $_ -notin $localKeys } | Select-Object -Unique)
    if ($missingKeys.Count -gt 0) {
        throw "Child control $($_.Name) uses parent resource(s) too early: $($missingKeys -join ', '). Use DynamicResource or define the resource locally."
    }
}

$appResourceKeys = @([regex]::Matches((Get-Content -LiteralPath (Join-Path $projectRoot 'App.xaml') -Raw), 'x:Key\s*=\s*"([^"]+)"') | ForEach-Object { $_.Groups[1].Value })
Get-ChildItem -LiteralPath $projectRoot -Filter '*Window.xaml' | ForEach-Object {
    $windowXaml = Get-Content -LiteralPath $_.FullName -Raw
    $localKeys = @([regex]::Matches($windowXaml, 'x:Key\s*=\s*"([^"]+)"') | ForEach-Object { $_.Groups[1].Value })
    $staticKeys = @([regex]::Matches($windowXaml, '\{StaticResource\s+([^}\s]+)') | ForEach-Object { $_.Groups[1].Value })
    $missingKeys = @($staticKeys | Where-Object { $_ -notin $localKeys -and $_ -notin $appResourceKeys } | Select-Object -Unique)
    if ($missingKeys.Count -gt 0) {
        throw "Popup $($_.Name) references unavailable static resource(s): $($missingKeys -join ', '). Publishing was stopped to prevent a popup crash."
    }
}

$fanCurveXaml = Get-Content -LiteralPath (Join-Path $projectRoot 'FanCurveWindow.xaml') -Raw
if ($fanCurveXaml -notmatch 'Path\s+Data="M 3,3 L 13,13 M 13,3 L 3,13"' -or
    $fanCurveXaml -match 'Content="&#xE8BB;"') {
    throw 'The fan-curve popup must use the vector close icon. Publishing was stopped to prevent a missing-glyph close button.'
}
if ($fanCurveXaml -notmatch '<ComboBox\.ItemTemplate>\s*<DataTemplate>\s*<TextBlock\s+Text="\{Binding DisplayName\}"') {
    throw 'The fan-curve temperature selector is missing its display-name template. Publishing was stopped to prevent internal class names appearing in the UI.'
}

$fanPageXaml = Get-Content -LiteralPath (Join-Path $projectRoot 'Controls\FanControlPage.xaml') -Raw
$fanViewModelText = Get-Content -LiteralPath (Join-Path $projectRoot 'ViewModels\FanControlViewModel.cs') -Raw
$fanServiceText = Get-Content -LiteralPath (Join-Path $projectRoot 'Services\FanControlService.cs') -Raw
$firmwareFanText = Get-Content -LiteralPath (Join-Path $projectRoot 'Services\FirmwareFanTelemetryProvider.cs') -Raw
foreach ($category in @('All', 'Case', 'CPU', 'GPU')) {
    if ($fanPageXaml -notmatch ('Tag="' + [regex]::Escape($category) + '"')) {
        throw "Fan Control is missing the $category filter tab."
    }
}
if ($fanPageXaml -notmatch 'ItemsSource="\{Binding FilteredChannels\}"' -or
    $fanViewModelText -notmatch 'ApplyCategoryFilter\(\)') {
    throw 'Fan Control tabs are not connected to the filtered fan collection.'
}
if ($fanPageXaml -notmatch 'ItemsSource="\{Binding FilteredSensors\}"' -or
    $fanViewModelText -notmatch 'sensors\.Where\(IsRelatedSensor\)' -or
    $fanViewModelText -notmatch 'FilteredChannels\.Any\(channel => channel\.IsRelatedSensor\(sensor\)\)') {
    throw 'Fan temperature sources are not scoped to their related fan or active category.'
}
if ($fanViewModelText -notmatch 'if \(sensor\.Category == "Storage"\) return false;' -or
    $fanViewModelText -notmatch 'HardwareType\.Equals\("Storage"') {
    throw 'Fan Control does not explicitly exclude drive temperature sensors.'
}
if ($fanPageXaml -notmatch 'RepeatBehavior="Forever"' -or
    $fanPageXaml -notmatch 'RepeatBehavior="5x"' -or
    $fanPageXaml -notmatch 'Text="\{Binding CalibrationProgressText\}"' -or
    $fanViewModelText -notmatch 'CalibrationProgressText = \$"\{percent\}%";' -or
    $fanViewModelText -notmatch 'CalibrationCompleted = completed;') {
    throw 'The live calibration progress and completion animations are incomplete.'
}
if ($fanPageXaml -notmatch 'Style x:Key="CalibrationStatusPanel"' -or
    $fanPageXaml -notmatch 'Style x:Key="CalibrationProgressTile"' -or
    $fanPageXaml -notmatch '<Border Style="\{StaticResource CalibrationStatusPanel\}">' -or
    $fanPageXaml -notmatch '<Border Grid.Column="1" Style="\{StaticResource CalibrationProgressTile\}">') {
    throw 'The top-right calibration status rectangle or separate percentage tile is missing.'
}
if ($fanViewModelText -notmatch 'FanCalibration\.json' -or
    $fanViewModelText -notmatch 'savedCalibrations\.TryGetValue\(device\.Id' -or
    $fanViewModelText -notmatch 'records\[channel\.Device\.Id\] = channel\.ToCalibration\(\);' -or
    $fanViewModelText -notmatch 'File\.Move\(temporaryPath, _calibrationPath, true\);') {
    throw 'Automatic fan calibration persistence is incomplete or is not using an atomic settings update.'
}
if ($fanServiceText -notmatch 'ApplyFirmwareRpmFallback\(force: true\)' -or
    $fanServiceText -notmatch 'HasUsableNativeRpm' -or
    $firmwareFanText -notmatch 'CIM_NumericSensor WHERE SensorType = 5' -or
    $firmwareFanText -notmatch 'Win32_Fan WHERE ActiveCooling = TRUE') {
    throw 'The safe firmware RPM fallback is incomplete.'
}
if ($fanPageXaml -notmatch 'Grid\.Row="4" Text="\{Binding CalibrationText\}"' -or
    $fanPageXaml -notmatch 'Style x:Key="CalibrationCardStatus"[^\r\n]*FontSize" Value="13"' -or
    $fanPageXaml -notmatch 'Binding CalibrationState.*Value="Calibrated"' -or
    $fanPageXaml -notmatch 'Binding CalibrationState.*Value="Warning"') {
    throw 'The per-fan calibration result is not positioned or color-coded correctly.'
}
$fanWording = $mainXaml + $fanPageXaml + (Get-Content -LiteralPath (Join-Path $projectRoot 'ChangelogWindow.xaml') -Raw)
if ($fanWording -match 'Fan Control\s*\(BETA\)' -or $fanPageXaml -match '>BETA<') {
    throw 'Fan Control still contains beta wording.'
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET 10 SDK is required. Install it, then run this script again.'
}

$sdkVersion = & dotnet --version
if ($LASTEXITCODE -ne 0 -or [version]($sdkVersion.Split('-')[0]) -lt [version]'10.0.0') {
    throw "SystemPulse requires the .NET 10 SDK. Detected: $sdkVersion"
}

& dotnet restore $project --runtime win-x64 --ignore-failed-sources
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
