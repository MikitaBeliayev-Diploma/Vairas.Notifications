param([Parameter(Mandatory)][string]$AuthenticationResourceGroup,[Parameter(Mandatory)][string]$AuthenticationApp)
$ErrorActionPreference='Stop'
if (-not $env:VAIRAS_WORKLOAD_SECRET -or $env:VAIRAS_WORKLOAD_SECRET.Length -lt 32) { throw 'Supply the same VAIRAS_WORKLOAD_SECRET used by Notifications.' }
$authProject=Join-Path (Split-Path $PSScriptRoot -Parent) '../Vairas.Authentication/Vairas.Authentication'
dotnet build $authProject -c Release --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Authentication hash provisioning tool build failed.' }
$hash=$env:VAIRAS_WORKLOAD_SECRET | & dotnet run --no-build -c Release --project $authProject -- hash-secret
if ($LASTEXITCODE -ne 0 -or $hash -notmatch '^\$argon2') { throw 'Workload credential hashing failed.' }
& az containerapp secret set --resource-group $AuthenticationResourceGroup --name $AuthenticationApp --secrets "notifications-secret-hash=$hash" -o none
if ($LASTEXITCODE -ne 0) { throw 'Authentication credential registration failed.' }
$scopes='identity.validate identity.contact.read profiles.contact.read school.notifications.read scheduling.notifications.read learning.notifications.read'.Split(' ')
$settings=@('Authentication__WorkloadClients__notifications__SecretHash=secretref:notifications-secret-hash')
for ($i=0;$i -lt $scopes.Length;$i++) { $settings+="Authentication__WorkloadClients__notifications__Scopes__$i=$($scopes[$i])" }
$audiences=& az containerapp show --resource-group $AuthenticationResourceGroup --name $AuthenticationApp --query "properties.template.containers[0].env[?starts_with(name, 'Authentication__UserAudiences__')].{name:name,value:value}" -o json | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw 'Could not inspect explicit Authentication audiences.' }
if ($audiences.Count -gt 0 -and 'vairas-notifications' -notin $audiences.value) {
    $index=1+($audiences | ForEach-Object { [int]($_.name.Split('_')[-1]) } | Measure-Object -Maximum).Maximum
    $settings+="Authentication__UserAudiences__$index=vairas-notifications"
}
& az containerapp update --resource-group $AuthenticationResourceGroup --name $AuthenticationApp --set-env-vars @settings -o none
if ($LASTEXITCODE -ne 0) { throw 'Authentication scope/audience configuration failed.' }
Write-Host 'Registered Notifications workload credential/scopes. Deploy the updated provider images before enabling workers.'
