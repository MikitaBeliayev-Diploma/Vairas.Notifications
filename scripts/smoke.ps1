param([Parameter(Mandatory)][string]$Origin,[Parameter(Mandatory)][Guid]$SchoolId)
$ErrorActionPreference='Stop'
if (-not $env:VAIRAS_SMOKE_USER_TOKEN) { throw 'Supply VAIRAS_SMOKE_USER_TOKEN outside source control.' }
$originUri=[Uri]$Origin
if (-not $originUri.IsAbsoluteUri -or $originUri.Scheme -ne 'https' -or $originUri.UserInfo) { throw 'Supply an HTTPS Notifications origin.' }
$response=Invoke-WebRequest -UseBasicParsing "$($Origin.TrimEnd('/'))/api/v1/schools/$SchoolId/notification-templates" -Headers @{ Authorization="Bearer $env:VAIRAS_SMOKE_USER_TOKEN"; 'X-Correlation-ID'='notifications-production-smoke' }
if ($response.StatusCode -ne 200) { throw 'Authorized school read failed.' }
Write-Host 'PASS: external authenticated Notifications read with current Auth/UM/School admission.'
