$ErrorActionPreference='Stop'
$k=(Get-Content $env:USERPROFILE\.emby-spike-key).Trim()
$sp=Join-Path $env:TEMP 'label-maker-spike'; New-Item -ItemType Directory -Force $sp | Out-Null
$base='http://localhost:8096/emby'
$H=@{'X-Emby-Token'=$k}
$pw=[Guid]::NewGuid().ToString('N')+'Aa1!'
$user=$null
try {
  $user=Invoke-RestMethod -Method Post -Headers $H -ContentType 'application/json' -Body (@{Name='lm-spike-admin'}|ConvertTo-Json) "$base/Users/New"
  Invoke-RestMethod -Method Post -Headers $H -ContentType 'application/json' -Body (@{NewPw=$pw;CurrentPw=''}|ConvertTo-Json) "$base/Users/$($user.Id)/Password" | Out-Null
  $pol=Invoke-RestMethod -Headers $H "$base/Users/$($user.Id)"
  $p=$pol.Policy; $p.IsAdministrator=$true
  Invoke-RestMethod -Method Post -Headers $H -ContentType 'application/json' -Body ($p|ConvertTo-Json -Depth 6) "$base/Users/$($user.Id)/Policy" | Out-Null
  $auth=Invoke-RestMethod -Method Post -ContentType 'application/json' -Headers @{'X-Emby-Authorization'='MediaBrowser Client="lm-spike", Device="probe", DeviceId="probe-ws-1", Version="1"'} -Body (@{Username='lm-spike-admin';Pw=$pw}|ConvertTo-Json) "$base/Users/AuthenticateByName"
  $tok=$auth.AccessToken
  $ws=New-Object System.Net.WebSockets.ClientWebSocket
  $ct=[Threading.CancellationToken]::None
  $ws.ConnectAsync([Uri]"ws://localhost:8096/embywebsocket?api_key=$tok&deviceId=probe-ws-1",$ct).Wait()
  "ws state: " + $ws.State
  Start-Sleep 2
  Invoke-RestMethod -Headers $H "$base/LabelMakerProbe/Run?Dir=$sp\spike\fx" | Out-Null
  $buf=New-Object byte[] 16384
  $seen=@()
  for($i=0;$i -lt 6;$i++){
    $t=$ws.ReceiveAsync([ArraySegment[byte]]$buf,$ct)
    if($t.Wait(2500)){ $m=[Text.Encoding]::UTF8.GetString($buf,0,$t.Result.Count); $seen += $m.Substring(0,[Math]::Min(200,$m.Length)) } else { break }
  }
  "messages received: " + $seen.Count
  $seen
  $ws.Dispose()
} finally {
  if($user){ Invoke-RestMethod -Method Delete -Headers $H "$base/Users/$($user.Id)" | Out-Null; "throw-away user deleted" }
}
