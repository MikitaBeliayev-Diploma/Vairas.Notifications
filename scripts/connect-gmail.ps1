param([Parameter(Mandatory)][string]$ClientId)
$ErrorActionPreference='Stop'
if (-not $env:GMAIL_CLIENT_SECRET) { throw 'Supply GMAIL_CLIENT_SECRET through the environment. Use a Google Desktop OAuth client with Gmail API enabled.' }
function Base64Url([byte[]]$Bytes) { return [Convert]::ToBase64String($Bytes).TrimEnd('=').Replace('+','-').Replace('/','_') }
$random=[Security.Cryptography.RandomNumberGenerator]::Create()
$bytes=New-Object byte[] 32; $random.GetBytes($bytes); $verifier=Base64Url $bytes
$random.GetBytes($bytes); $state=Base64Url $bytes; $random.Dispose()
$sha=[Security.Cryptography.SHA256]::Create(); $challenge=Base64Url ($sha.ComputeHash([Text.Encoding]::ASCII.GetBytes($verifier))); $sha.Dispose()
$listener=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
$listener.Start(); $port=([Net.IPEndPoint]$listener.LocalEndpoint).Port
$redirect="http://127.0.0.1:$port/"
$query=@{ client_id=$ClientId; redirect_uri=$redirect; response_type='code'; access_type='offline'; prompt='consent'; state=$state
    code_challenge=$challenge; code_challenge_method='S256'; scope='https://www.googleapis.com/auth/gmail.send https://www.googleapis.com/auth/gmail.readonly' }
$authorization='https://accounts.google.com/o/oauth2/v2/auth?'+(($query.GetEnumerator() | ForEach-Object { [Uri]::EscapeDataString($_.Key)+'='+[Uri]::EscapeDataString($_.Value) }) -join '&')
try {
    Write-Host 'Authorize the dedicated sender in your browser. Credentials and authorization codes will not be printed.'
    Start-Process $authorization
    $accepted=$listener.AcceptTcpClientAsync()
    if (-not $accepted.Wait(600000)) { throw 'Google authorization timed out.' }
    $client=$accepted.Result
    $client.ReceiveTimeout=10000
    try {
        $stream=$client.GetStream(); $reader=[IO.StreamReader]::new($stream,[Text.Encoding]::ASCII,$false,1024,$true)
        $line=$reader.ReadLine(); $reader.Dispose()
        if ($line -notmatch '^GET /\?([^ ]+) HTTP/') { throw 'Invalid loopback OAuth callback.' }
        $values=@{}
        foreach ($pair in $Matches[1].Split('&')) { $parts=$pair.Split('=',2); if ($parts.Count -eq 2) { $values[[Uri]::UnescapeDataString($parts[0])]=[Uri]::UnescapeDataString($parts[1].Replace('+',' ')) } }
        if ($values.state -cne $state -or -not $values.code -or $values.error) { throw 'Google authorization was denied or callback state did not match.' }
        $page=[Text.Encoding]::UTF8.GetBytes('Authorization received. You may close this window.')
        $headers=[Text.Encoding]::ASCII.GetBytes("HTTP/1.1 200 OK`r`nContent-Type: text/plain; charset=utf-8`r`nCache-Control: no-store`r`nConnection: close`r`nContent-Length: $($page.Length)`r`n`r`n")
        $stream.Write($headers,0,$headers.Length); $stream.Write($page,0,$page.Length); $stream.Flush()
    } finally { $client.Dispose() }
    $tokens=Invoke-RestMethod -Method Post -Uri 'https://oauth2.googleapis.com/token' -Body @{ code=$values.code; code_verifier=$verifier; client_id=$ClientId
        client_secret=$env:GMAIL_CLIENT_SECRET; redirect_uri=$redirect; grant_type='authorization_code' }
    if (-not $tokens.refresh_token -or -not $tokens.access_token) { throw 'Google did not issue offline authorization. Recheck consent/client configuration.' }
    $profile=Invoke-RestMethod 'https://gmail.googleapis.com/gmail/v1/users/me/profile' -Headers @{ Authorization="Bearer $($tokens.access_token)" }
    if (-not $profile.emailAddress) { throw 'Gmail sender identity could not be verified.' }
    $root=Split-Path $PSScriptRoot -Parent
    @{ Gmail=@{ ClientId=$ClientId; ClientSecret=$env:GMAIL_CLIENT_SECRET; RefreshToken=$tokens.refresh_token; Sender=$profile.emailAddress; Enabled=$false } } |
        ConvertTo-Json -Depth 5 | & dotnet user-secrets set --project (Join-Path $root 'Vairas.Notifications') | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'User-secrets storage failed.' }
    $env:GMAIL_REFRESH_TOKEN=$tokens.refresh_token
    Write-Host 'Gmail authorization saved in user-secrets and GMAIL_REFRESH_TOKEN for this PowerShell process. Workers remain disabled until configured.'
} finally { $listener.Stop() }
