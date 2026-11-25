param([string]$Image = 'vairas-notifications:local', [switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
$suffix = [Guid]::NewGuid().ToString('N')
$network = "nt-check-$suffix"
$database = "nt-pg-$suffix"
$api = "nt-api-$suffix"
$password = [Guid]::NewGuid().ToString('N')
$runtimePassword = [Guid]::NewGuid().ToString('N')
$migrationPassword = [Guid]::NewGuid().ToString('N')
$workerPassword = [Guid]::NewGuid().ToString('N')
function Invoke-Docker([string[]]$DockerArguments) {
    & docker @DockerArguments
    if ($LASTEXITCODE -ne 0) { throw "Docker command failed: $($DockerArguments[0])" }
}
function Status([string]$Url) {
    try { return [int](Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 2).StatusCode }
    catch { if ($_.Exception.Response) { return [int]$_.Exception.Response.StatusCode }; return 0 }
}
try {
    if (-not $SkipBuild) { Invoke-Docker @('build', '--tag', $Image, '.') }
    Invoke-Docker @('network', 'create', $network) | Out-Null
    Invoke-Docker @('run', '-d', '--name', $database, '--network', $network, '-e', "POSTGRES_PASSWORD=$password", 'postgres:17') | Out-Null
    $databaseReady = $false
    for ($i = 0; $i -lt 60; $i++) {
        & docker exec $database pg_isready -U postgres -h 127.0.0.1 *> $null
        if ($LASTEXITCODE -eq 0) { $databaseReady = $true; break }
        Start-Sleep -Milliseconds 500
    }
    if (-not $databaseReady) { throw 'PostgreSQL did not start.' }
    $provisionConnection = "Host=$database;Database=postgres;Username=postgres;Password=$password;GSS Encryption Mode=Disable"
    foreach ($iteration in 1..2) {
        Invoke-Docker @('run','--rm','--network',$network,'-e',"VAIRAS_PROVISION_CONNECTION=$provisionConnection",'-e',"VAIRAS_MIGRATION_PASSWORD=$migrationPassword",
            '-e',"VAIRAS_RUNTIME_PASSWORD=$runtimePassword",'-e',"VAIRAS_WORKER_PASSWORD=$workerPassword",$Image,'--initialize')
    }
    $migrationConnection = "Host=$database;Database=vairas_notifications;Username=v_notifications_migration;Password=$migrationPassword;GSS Encryption Mode=Disable"
    foreach ($action in @('--migrate', '--grant-runtime', '--grant-worker')) {
        Invoke-Docker @('run', '--rm', '--network', $network, '-e', "VAIRAS_MIGRATION_CONNECTION=$migrationConnection", $Image, $action)
    }
    $runtimeConnection = "Host=$database;Database=vairas_notifications;Username=v_notifications_runtime;Password=$runtimePassword;GSS Encryption Mode=Disable"
    Invoke-Docker @('run', '-d', '--name', $api, '--network', $network, '-p', '127.0.0.1::8080', '-e', 'AllowedHosts=127.0.0.1', '-e', "ConnectionStrings__Notifications=$runtimeConnection", $Image) | Out-Null
    $binding = & docker port $api 8080
    if ($LASTEXITCODE -ne 0) { throw 'API port lookup failed.' }
    $origin = "http://$($binding.Trim())"
    $ready = $false
    for ($i = 0; $i -lt 60; $i++) {
        if ((Status "$origin/health/ready") -eq 200) { $ready = $true; break }
        Start-Sleep -Milliseconds 500
    }
    if (-not $ready) { & docker logs --tail 20 $api; throw 'Published API did not become ready.' }
    if ((Status "$origin/health/live") -ne 200) { throw 'Liveness failed.' }
    if ((Status "$origin/api/v1/schools/$suffix/notification-templates") -ne 401) { throw 'Anonymous read was not denied.' }
    if ((Status "$origin/openapi/v1.json") -ne 404) { throw 'Production OpenAPI exposure differs from the deployment contract.' }
    Write-Host 'PASS: published provisioning/initialization twice, migrations, API/worker grants, restricted runtime readiness, anonymous denial and production route surface.'
}
finally {
    & docker rm -f $api $database 2>$null | Out-Null
    & docker network rm $network 2>$null | Out-Null
    Pop-Location
}
