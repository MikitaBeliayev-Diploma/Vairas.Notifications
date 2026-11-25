param([switch]$SkipIntegration)
$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    dotnet restore Vairas.Notifications.slnx --force
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    dotnet build Vairas.Notifications.slnx --no-restore -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    $testArgs = @('test', 'Vairas.Notifications.slnx', '--no-build', '-c', 'Release', '--settings', 'coverage.runsettings', '--collect', 'XPlat Code Coverage', '--results-directory', 'TestResults/verify', '--logger', 'trx;LogFileName=verification.trx')
    # Explicit collector path also works when --no-build bypasses collector discovery hooks.
    $packageCache = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget/packages' }
    $testArgs += @('--test-adapter-path', (Join-Path $packageCache 'coverlet.collector/6.0.4/build/netstandard2.0'))
    if ($SkipIntegration) { $testArgs += @('--filter', 'TestCategory=Unit') }
    else { $testArgs += @('--filter', 'TestCategory!=Broker') }
    & dotnet @testArgs
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    $report = Get-ChildItem TestResults/verify -Recurse -Filter coverage.cobertura.xml | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $report) { throw 'Coverage report is missing.' }
    [xml]$coverage = Get-Content -LiteralPath $report.FullName
    $rates = @{}
    foreach ($package in $coverage.coverage.packages.package) {
        $rate = [double]::Parse($package.'line-rate', [Globalization.CultureInfo]::InvariantCulture)
        $rates[$package.name] = $rate
        Write-Host ("{0}: {1:P2} line coverage" -f $package.name, $rate)
    }
    if ($SkipIntegration) { Write-Warning 'Partial verification: PostgreSQL/HTTP integration and Business coverage gate were not run.' }
    else {
        foreach ($assembly in @('Vairas.Notifications.Domain', 'Vairas.Notifications.Business')) {
            if (-not $rates.ContainsKey($assembly) -or $rates[$assembly] -lt 0.70) { throw "$assembly line coverage is below 70%." }
        }
        New-Item -ItemType Directory -Force evidence | Out-Null
        Copy-Item -LiteralPath $report.FullName -Destination evidence/coverage.cobertura.xml
    }
}
finally { Pop-Location }
