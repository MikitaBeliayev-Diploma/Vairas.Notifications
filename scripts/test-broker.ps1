param([switch]$AcceptMicrosoftEula)
$ErrorActionPreference='Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
$project='vairas-notifications-broker-'+[Guid]::NewGuid().ToString('N').Substring(0,10)
$compose=Join-Path $PWD 'tests/servicebus/docker-compose.yaml'
$started=$false
$previousConnection=$env:VAIRAS_TEST_SERVICEBUS_CONNECTION
try {
    if (-not $env:VAIRAS_TEST_SERVICEBUS_CONNECTION) {
        if (-not $AcceptMicrosoftEula) { throw 'Local Microsoft SQL Server/Service Bus emulator requires -AcceptMicrosoftEula after reviewing the Microsoft license terms linked in README.' }
        $env:ACCEPT_EULA='Y'; $env:MSSQL_SA_PASSWORD='Synthetic!'+[Guid]::NewGuid().ToString('N')
        docker compose -p $project -f $compose up -d
        if ($LASTEXITCODE -ne 0) { throw 'Emulator startup failed.' }; $started=$true
        $ready=$false
        for ($i=0;$i -lt 60;$i++) {
            try { if ((Invoke-WebRequest 'http://127.0.0.1:15301/health' -UseBasicParsing -TimeoutSec 2).StatusCode -eq 200) { $ready=$true; break } } catch { }
            Start-Sleep -Seconds 2
        }
        if (-not $ready) { throw 'Service Bus emulator did not become healthy.' }
        $env:VAIRAS_TEST_SERVICEBUS_CONNECTION='Endpoint=sb://localhost:15672;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;'
    }
    dotnet restore Vairas.Notifications.slnx --force
    if ($LASTEXITCODE -ne 0) { throw 'Broker test restore failed.' }
    dotnet test Vairas.Notifications.Tests/Vairas.Notifications.Tests.csproj -c Release --no-restore --filter 'TestCategory=Broker|FullyQualifiedName~SixServiceTests' --logger 'trx;LogFileName=broker.trx' --results-directory TestResults/broker
    if ($LASTEXITCODE -ne 0) {
        if ($started) { docker compose -p $project -f $compose logs --no-color emulator | Out-File -Encoding utf8 TestResults/broker/emulator-failure.log }
        throw 'Broker tests failed.'
    }
} finally {
    if ($started) { docker compose -p $project -f $compose down -v }
    $env:VAIRAS_TEST_SERVICEBUS_CONNECTION=$previousConnection
    Remove-Item Env:MSSQL_SA_PASSWORD,Env:ACCEPT_EULA -ErrorAction SilentlyContinue
    Pop-Location
}
