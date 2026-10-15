<#
Live regression test for the Label Maker plugin on a throw-away Emby Server of its own (never the shared test server).
It copies the Emby program folder to a temp folder, starts that copy on its own port with an empty data folder, completes Emby's
first-run wizard through its API, deploys the built DLL, runs the whole flow with real tagged files and the real MusicBrainz, watches
the phone alerts through an administrator websocket, loads the page script the plugin serves in a real browser, and then deletes
everything it made. Nothing outside its temp folder is touched and no existing Emby user, library or setting is involved.

  powershell -ExecutionPolicy Bypass -File tools\Test-LabelMakerPlugin.ps1 [-DllPath <path>] [-Port 8196] [-KeepRunning]

Needs: an unpacked Emby Server (the folder that holds `system\EmbyServer.exe`; default is the one in Downloads), network access to
musicbrainz.org, and Google Chrome for the page check. Exit code 0 only when every check passed.
With -KeepRunning the instance and its login are left up (the login is printed) so the page can be tried by hand; stop it with
tools\Stop-LabelMakerTestInstance.ps1.
#>
param(
  [string]$DllPath = '',
  [string]$ServerRoot = (Join-Path $env:USERPROFILE 'Downloads\embyserver-win-x64-4.10.1.0'),
  [int]$Port = 8196,
  [switch]$KeepRunning
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
[Environment]::CurrentDirectory = $repo
if ($DllPath -eq '') { $DllPath = Join-Path $repo 'src\LabelMaker\bin\Release\net6.0\LabelMaker.dll' }
if (-not (Test-Path $DllPath)) { throw "Build first: $DllPath not found." }
if (-not (Test-Path (Join-Path $ServerRoot 'system\EmbyServer.exe'))) { throw "No Emby Server found at $ServerRoot (system\EmbyServer.exe)." }

$base = "http://127.0.0.1:$Port/emby"
$pluginId = 'a29bf822-41ab-4e14-a814-a8d386e7f745'
$stamp = [Guid]::NewGuid().ToString('N').Substring(0, 8)
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("lm-live-$stamp")
$rigSystem = Join-Path $tempRoot 'system'
$rigData = Join-Path $tempRoot 'programdata'
$inbox = Join-Path $tempRoot 'inbox'
$library = Join-Path $tempRoot 'library'
$results = New-Object System.Collections.ArrayList
$script:rigProcessId = 0
$ws = $null
$script:pending = $null
$script:buf = New-Object byte[] 65536   # one buffer for the whole run: a read that is still waiting writes into it
$script:H = @{}
$script:Token = ''
$script:Password = ''

function Check([string]$name, [bool]$ok, [string]$detail = '') {
  [void]$results.Add([pscustomobject]@{ Name = $name; Ok = $ok; Detail = $detail })
  if ($ok) { Write-Host ("  ok   " + $name) } else { Write-Host ("  FAIL " + $name + "  " + $detail) -ForegroundColor Red }
}
function Api([string]$method, [string]$path, $body = $null, $headers = $script:H) {
  $args2 = @{ Method = $method; Uri = "$base$path"; Headers = $headers }
  if ($body -ne $null) { $args2.Body = ($body | ConvertTo-Json -Depth 8); $args2.ContentType = 'application/json' }
  Invoke-RestMethod @args2
}
function TryApi([string]$method, [string]$path, $body = $null) {
  try { return @{ Ok = $true; Data = (Api $method $path $body) } }
  catch {
    $text = ''
    try { $r = New-Object IO.StreamReader($_.Exception.Response.GetResponseStream()); $text = $r.ReadToEnd() } catch {}
    return @{ Ok = $false; Data = $text; Message = $_.Exception.Message }
  }
}
function WaitUntil([scriptblock]$cond, [int]$seconds, [int]$every = 2) {
  $end = (Get-Date).AddSeconds($seconds)
  while ((Get-Date) -lt $end) { if (& $cond) { return $true }; Start-Sleep $every }
  return $false
}

# ---- the throw-away Emby instance ------------------------------------------------------------------------------------------
function Start-Rig {
  Write-Host '-- starting a private Emby instance'
  $busy = netstat -ano | Select-String -Pattern (":$Port\s+.*LISTENING")
  if ($busy) { throw "Port $Port is already in use. Pass another one with -Port." }
  New-Item -ItemType Directory -Force $tempRoot, (Join-Path $rigData 'config'), (Join-Path $rigData 'plugins') | Out-Null
  Copy-Item (Join-Path $ServerRoot 'system') $rigSystem -Recurse -Force   # about 440 MB: Emby refuses to run a second copy from the same place
  Copy-Item $DllPath (Join-Path $rigData 'plugins\LabelMaker.dll')
  [IO.File]::WriteAllText((Join-Path $rigData 'config\system.xml'), @"
<?xml version="1.0" encoding="utf-8"?>
<ServerConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <HttpServerPortNumber>$Port</HttpServerPortNumber>
  <HttpsPortNumber>$($Port + 1)</HttpsPortNumber>
  <PublicPort>$Port</PublicPort>
  <PublicHttpsPort>$($Port + 1)</PublicHttpsPort>
  <EnableHttps>false</EnableHttps>
  <EnableUPnP>false</EnableUPnP>
  <AutoRunWebApp>false</AutoRunWebApp>
</ServerConfiguration>
"@, (New-Object Text.UTF8Encoding($false)))
  $exe = Join-Path $rigSystem 'EmbyServer.exe'
  $argList = @('-programdata', "`"$rigData`"", '-ffmpeg', "`"$(Join-Path $rigSystem 'ffmpeg.exe')`"", '-ffprobe', "`"$(Join-Path $rigSystem 'ffprobe.exe')`"", '-restartexitcode', '3')
  $proc = Start-Process $exe -ArgumentList $argList -PassThru -WindowStyle Hidden
  $script:rigProcessId = $proc.Id
  $up = WaitUntil { try { $null = Invoke-RestMethod "$base/System/Info/Public"; $true } catch { $false } } 180 3
  if (-not $up) { throw 'The private Emby instance did not come up.' }
  # Emby's first-run wizard, through its own API.
  $json = 'application/json'
  Invoke-RestMethod -Method Post -ContentType $json -Body '{"UICulture":"en-US","MetadataCountryCode":"US","PreferredMetadataLanguage":"en"}' "$base/Startup/Configuration" | Out-Null
  $script:Password = [Guid]::NewGuid().ToString('N') + 'Aa1!'
  try { Invoke-RestMethod "$base/Startup/User" | Out-Null } catch {}
  Invoke-RestMethod -Method Post -ContentType $json -Body (@{ Name = 'lm-rig-admin'; Password = $script:Password } | ConvertTo-Json) "$base/Startup/User" | Out-Null
  Invoke-RestMethod -Method Post -ContentType $json -Body '{"EnableRemoteAccess":false,"EnableAutomaticPortMapping":false}' "$base/Startup/RemoteAccess" | Out-Null
  Invoke-RestMethod -Method Post "$base/Startup/Complete" | Out-Null
  Start-Sleep 4
  $auth = Invoke-RestMethod -Method Post -ContentType $json -Headers @{ 'X-Emby-Authorization' = 'MediaBrowser Client="lm-live", Device="probe", DeviceId="lm-live-1", Version="1"' } -Body (@{ Username = 'lm-rig-admin'; Pw = $script:Password } | ConvertTo-Json) "$base/Users/AuthenticateByName"
  $script:Token = $auth.AccessToken
  $script:H = @{ 'X-Emby-Token' = $script:Token }
  $up2 = WaitUntil { try { (Api GET '/LabelMaker/Info').ApiLevel -eq 1 } catch { $false } } 120 3
  if (-not $up2) { throw 'The Label Maker plugin did not answer in the private instance.' }
  Start-Sleep 22   # the plugin waits 20 seconds before its first check
}
function Stop-Rig {
  if ($script:rigProcessId -gt 0) {
    try { Stop-Process -Id $script:rigProcessId -Force -ErrorAction SilentlyContinue } catch {}
    # Emby may start helper processes of its own from the private folder; stop only those.
    Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($rigSystem, [StringComparison]::OrdinalIgnoreCase) } | ForEach-Object { try { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue } catch {} }
    Start-Sleep 3
  }
}

# Reads websocket messages for a while; returns the LabelMaker.* ones as objects.
function ReadWs([int]$seconds) {
  $out = New-Object System.Collections.ArrayList
  $buf = $script:buf
  $end = (Get-Date).AddSeconds($seconds)
  while ((Get-Date) -lt $end) {
    if ($script:pending -eq $null) { $script:pending = $ws.ReceiveAsync([ArraySegment[byte]]$buf, [Threading.CancellationToken]::None) }
    if ($script:pending.Wait(800)) {
      $res = $script:pending.Result; $script:pending = $null
      $text = [Text.Encoding]::UTF8.GetString($buf, 0, $res.Count)
      try { $m = $text | ConvertFrom-Json; if ($m.MessageType -like 'LabelMaker.*') { [void]$out.Add($m) } } catch {}
    }
  }
  return $out
}
# ---- sockets that connect the way the Android app does: the token only in the X-Emby-Token header (and later inside the start message),
# ---- the device id in the address, never ?api_key= (a test that used api_key would prove nothing about the app) -------------------------
$script:appSockets = New-Object System.Collections.ArrayList
function New-AppSocket([string]$token, [string]$device) {
  $s = New-Object System.Net.WebSockets.ClientWebSocket
  if ($token) { $s.Options.SetRequestHeader('X-Emby-Token', $token) }
  $s.ConnectAsync([Uri]("ws://127.0.0.1:$Port/embywebsocket?deviceId=$device"), [Threading.CancellationToken]::None).Wait()
  $sock = @{ Ws = $s; Pending = $null; Buf = (New-Object byte[] 262144) }
  [void]$script:appSockets.Add($sock)
  return $sock
}
function Send-App($sock, [string]$text) {
  $b = [Text.Encoding]::UTF8.GetBytes($text)
  $sock.Ws.SendAsync([ArraySegment[byte]]$b, [Net.WebSockets.WebSocketMessageType]::Text, $true, [Threading.CancellationToken]::None).Wait()
}
# Reads for a while; returns the LabelMakerEvents messages (their Data objects) in the order they arrived.
function Read-App($sock, [int]$seconds) {
  $out = New-Object System.Collections.ArrayList
  $end = (Get-Date).AddSeconds($seconds)
  while ((Get-Date) -lt $end) {
    if ($sock.Pending -eq $null) { $sock.Pending = $sock.Ws.ReceiveAsync([ArraySegment[byte]]$sock.Buf, [Threading.CancellationToken]::None) }
    if ($sock.Pending.Wait(500)) {
      $res = $sock.Pending.Result; $sock.Pending = $null
      $text = [Text.Encoding]::UTF8.GetString($sock.Buf, 0, $res.Count)
      try { $m = $text | ConvertFrom-Json; if ($m.MessageType -eq 'LabelMakerEvents') { [void]$out.Add($m.Data) } } catch {}
    }
  }
  return $out
}
function Start-Message([string]$afterSeq, [string]$token) { '{"MessageType":"LabelMakerEventsStart","Data":"0,30000,' + $afterSeq + ',' + $token + '"}' }

function Tag-Mp3([string]$path, [string]$title, [int]$track) {
  $f = [TagLib.File]::Create($path)
  $f.Tag.Title = $title; $f.Tag.Performers = @('Green Day'); $f.Tag.AlbumArtists = @('Green Day'); $f.Tag.Album = 'Insomniac'
  $f.Tag.Track = $track; $f.Tag.TrackCount = 14; $f.Tag.Disc = 1; $f.Tag.DiscCount = 2; $f.Tag.Year = 2021
  $f.Save(); $f.Dispose()
}
function Add-Album([string]$root, [string]$artist) {
  $dir = Join-Path $root "Albums\$artist\Insomniac"
  New-Item -ItemType Directory -Force $dir | Out-Null
  $fixture = Join-Path $repo 'tests\LabelMaker.Tests\Fixtures\silence.mp3'
  Copy-Item $fixture (Join-Path $dir '02 - Brat.mp3'); Tag-Mp3 (Join-Path $dir '02 - Brat.mp3') 'Brat' 2
  Copy-Item $fixture (Join-Path $dir '07 - 86.mp3');   Tag-Mp3 (Join-Path $dir '07 - 86.mp3') '86' 7
}
function Count-Mp3([string]$root) { @(Get-ChildItem $root -Recurse -Filter *.mp3 -ErrorAction SilentlyContinue | Where-Object { $_.FullName -notmatch '_Trash' }).Count }
function Save-Config($lanes, [bool]$dryRun, [bool]$paused = $false) {
  $cfg = Api GET "/Plugins/$pluginId/Configuration"
  $cfg.Lanes = $lanes; $cfg.DryRun = $dryRun; $cfg.Paused = $paused
  $cfg.StableSeconds = 10; $cfg.PassSeconds = 3600; $cfg.AlertAutoApplied = $true
  Api POST "/Plugins/$pluginId/Configuration" $cfg | Out-Null
}
function Lane-Obj { @(@{ Id = 'live'; Name = 'Live test'; InboxPath = $inbox; LibraryPath = $library; AutoApply = $true }) }
function Run-Pass { $null = Api POST '/LabelMaker/Run'; Start-Sleep 1 }
function Status { Api GET '/LabelMaker/Status' }
function Count-Of([string]$state) { [int](Status).Counts.$state }

try {
  Write-Host "== Label Maker live test ($DllPath)"
  Add-Type -Path (Join-Path $repo 'tools\TagLibSharp.dll')

  Write-Host '-- the page script, before anything starts'
  $pre = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Check-PageScript.ps1') 2>&1 | Out-String
  Check 'The page script in the source loads in a real browser without errors' ($LASTEXITCODE -eq 0) $pre.Trim()

  Start-Rig

  Write-Host '-- routes'
  $info = Api GET '/LabelMaker/Info'
  Check 'Info has a version and ApiLevel 1' ($info.PluginVersion -and $info.ApiLevel -eq 1) ($info | ConvertTo-Json -Compress)
  Check 'Info lists the features' ((@($info.Features) -contains 'events') -and (@($info.Features) -contains 'catalog') -and (@($info.Features) -contains 'status'))
  $cat = Api GET '/LabelMaker/Notifications/Catalog'
  Check 'Catalog has the five alert rules' (@($cat.Events).Count -eq 5 -and $cat.Service.ChannelId -eq 'music-ingest') ((@($cat.Events) | ForEach-Object { $_.Id }) -join ',')
  $st = Status
  Check 'Status has the counts, lanes, service and LatestSeq' ($st.Counts -and $st.Service.MusicBrainz -eq 'Ok' -and $st.LatestSeq -ne $null -and $st.MatchingVersion)
  $page = Invoke-WebRequest -UseBasicParsing -Headers $script:H "$base/web/configurationpage?name=LabelMakerConfiguration"
  Check 'The settings page is served' ($page.Content -match 'Label Maker')
  # The exact script and markup the plugin serves, loaded in a real browser: a script error here is the "menu item does nothing" failure.
  $served = Join-Path $tempRoot 'served'
  New-Item -ItemType Directory -Force $served | Out-Null
  Invoke-WebRequest -UseBasicParsing -Headers $script:H "$base/web/configurationpage?name=LabelMakerConfigurationjs" -OutFile (Join-Path $served 'page.js')
  Invoke-WebRequest -UseBasicParsing -Headers $script:H "$base/web/configurationpage?name=LabelMakerConfiguration" -OutFile (Join-Path $served 'page.html')
  $pageOut = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Check-PageScript.ps1') -ScriptPath (Join-Path $served 'page.js') -HtmlPath (Join-Path $served 'page.html') 2>&1 | Out-String
  Check 'The page script the plugin serves loads in a real browser without errors' ($LASTEXITCODE -eq 0) $pageOut.Trim()
  $anon = $false; try { Invoke-RestMethod "$base/LabelMaker/Status" | Out-Null } catch { $anon = $true }
  Check 'A request with no token is refused' $anon

  Write-Host '-- settings are checked when saved'
  $bad = TryApi POST "/Plugins/$pluginId/Configuration" ((Api GET "/Plugins/$pluginId/Configuration") | ForEach-Object { $_.Lanes = @(@{ Id = 'x'; Name = 'X'; InboxPath = 'relative\path'; LibraryPath = 'D:\lib'; AutoApply = $true }); $_ })
  Check 'A lane with a relative path is refused with a plain message' ((-not $bad.Ok) -and ($bad.Data -match 'full path' -or $bad.Message -match '400|500')) ([string]$bad.Data)
  $badScore = TryApi POST "/Plugins/$pluginId/Configuration" ((Api GET "/Plugins/$pluginId/Configuration") | ForEach-Object { $_.AutoThreshold = 0.2; $_ })
  Check 'A threshold out of range is refused' (-not $badScore.Ok)

  Write-Host '-- administrator websocket'
  $ws = New-Object System.Net.WebSockets.ClientWebSocket
  $ws.ConnectAsync([Uri]("ws://127.0.0.1:$Port/embywebsocket?api_key=$($script:Token)&deviceId=lm-live-1"), [Threading.CancellationToken]::None).Wait()
  Check 'The websocket connected' ($ws.State -eq 'Open')

  Write-Host '-- the events subscription, the way the app connects'
  $app = New-AppSocket $script:Token 'lm-app-1'
  Check 'The app-style socket connected (token in the header only)' ($app.Ws.State -eq 'Open')
  Send-App $app (Start-Message '' $script:Token)
  $baseline = @(Read-App $app 8)
  Check 'Subscribing with an empty afterSeq answers with a baseline only (LatestSeq, no events)' (@($baseline).Count -ge 1 -and $baseline[0].ApiLevel -eq 1 -and $baseline[0].LatestSeq -ge 0 -and $baseline[0].Truncated -eq $false -and @($baseline[0].Events).Count -eq 0) (($baseline | ConvertTo-Json -Compress -Depth 4))
  $script:baselineSeq = [long]$baseline[0].LatestSeq
  # A signed-in user who is not an administrator, a token nobody has, and no token at all: each gets the error and nothing more.
  $userPw = [Guid]::NewGuid().ToString('N') + 'Aa1!'
  $plainUser = Api POST '/Users/New' @{ Name = 'lm-rig-user' }
  Api POST "/Users/$($plainUser.Id)/Password" @{ NewPw = $userPw; CurrentPw = '' } | Out-Null
  $userAuth = Api POST '/Users/AuthenticateByName' @{ Username = 'lm-rig-user'; Pw = $userPw } @{ 'X-Emby-Authorization' = 'MediaBrowser Client="lm-live", Device="probe", DeviceId="lm-live-user", Version="1"' }
  $plainSocket = New-AppSocket $userAuth.AccessToken 'lm-live-user'
  Send-App $plainSocket (Start-Message '' $userAuth.AccessToken)
  $plainAnswer = @(Read-App $plainSocket 6)
  Check 'A signed-in user who is not an administrator is refused' (@($plainAnswer).Count -eq 1 -and $plainAnswer[0].Error -eq 'Administrator login required.' -and -not $plainAnswer[0].Events) (($plainAnswer | ConvertTo-Json -Compress -Depth 3))
  $junkToken = [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N')
  $junkSocket = New-AppSocket $script:Token 'lm-app-junk'
  Send-App $junkSocket (Start-Message '' $junkToken)
  $junkAnswer = @(Read-App $junkSocket 6)
  Check 'A token nobody has is refused' (@($junkAnswer).Count -eq 1 -and $junkAnswer[0].Error -eq 'Administrator login required.') (($junkAnswer | ConvertTo-Json -Compress -Depth 3))
  $noTokenSocket = New-AppSocket $script:Token 'lm-app-none'
  Send-App $noTokenSocket '{"MessageType":"LabelMakerEventsStart","Data":"0,30000,"}'
  $noTokenAnswer = @(Read-App $noTokenSocket 6)
  Check 'A start message with no token is refused' (@($noTokenAnswer).Count -eq 1 -and $noTokenAnswer[0].Error -eq 'Administrator login required.') (($noTokenAnswer | ConvertTo-Json -Compress -Depth 3))

  Write-Host '-- a lane, a dry run, and the alerts'
  New-Item -ItemType Directory -Force $inbox, $library | Out-Null
  Save-Config (Lane-Obj) $true
  $pre2 = Api GET '/LabelMaker/Preflight'
  $laneResult = @($pre2.Lanes)[0]
  Check 'Preflight passes for the new lane' ($laneResult.CanRun -eq $true) (($laneResult.Checks | Where-Object { $_.Level -eq 'Error' } | ForEach-Object { $_.Message }) -join ' | ')
  Add-Album $inbox 'Green Day'
  Run-Pass; Start-Sleep 12; Run-Pass
  $ok = WaitUntil { (Count-Of 'Ready') -ge 1 } 120
  Check 'The album became Ready (dry run) after matching on MusicBrainz' $ok ((Status).Counts | ConvertTo-Json -Compress)
  $msgs = ReadWs 6
  Check 'A WouldApply alert was pushed to the administrator websocket' (@($msgs | Where-Object { $_.MessageType -eq 'LabelMaker.AutoApplied' -and $_.Data.Mode -eq 'WouldApply' }).Count -eq 1) (($msgs | ForEach-Object { $_.MessageType }) -join ',')
  Check 'Every alert carries the common fields' (@($msgs | Where-Object { $_.Data.ApiLevel -eq 1 -and $_.Data.Seq -gt 0 -and $_.Data.EventId -and $_.Data.ServerTimeUtc -and $_.Data.Event }).Count -eq @($msgs).Count -and @($msgs).Count -gt 0)
  $raw = ($msgs | ConvertTo-Json -Depth 8)
  Check 'No alert carries a folder path' (($raw -notmatch [regex]::Escape($tempRoot)) -and ($raw -notmatch 'lm-live-'))
  Check 'Dry run changed nothing in the library' ((Count-Of 'Done') -eq 0 -and (Count-Mp3 $library) -eq 0 -and (Count-Mp3 $inbox) -eq 2)

  # What the app's subscription received while all that happened: promptly, in order, once each.
  $appMessages = @(Read-App $app 3)
  $appEvents = @($appMessages | ForEach-Object { @($_.Events) } | Where-Object { $_ })
  $appSeqs = @($appEvents | ForEach-Object { [long]$_.Seq })
  Check 'The subscription pushed the new alerts with the same shape as the catch-up route' (@($appEvents | Where-Object { $_.Name -eq 'LabelMaker.AutoApplied' -and $_.Data.Mode -eq 'WouldApply' -and $_.AtUtc -and $_.Data.Seq -eq $_.Seq }).Count -ge 1) (($appMessages | ConvertTo-Json -Compress -Depth 5))
  Check 'Every pushed event is newer than the baseline, in order, and none came twice' ($appSeqs.Count -gt 0 -and @($appSeqs | Where-Object { $_ -le $script:baselineSeq }).Count -eq 0 -and ((@($appSeqs | Sort-Object -Unique).Count) -eq $appSeqs.Count) -and (($appSeqs -join ',') -eq ((@($appSeqs | Sort-Object)) -join ','))) ($appSeqs -join ',')
  Check 'Each push reports the latest number' (@($appMessages | Where-Object { $_.LatestSeq -ge ($appSeqs | Measure-Object -Maximum).Maximum }).Count -ge 1)

  $ev = Api GET '/LabelMaker/Events?After=0&Limit=50'
  Check 'The catch-up log has the alerts in order' (@($ev.Events).Count -ge 2 -and ($ev.Events[0].Seq -lt $ev.Events[-1].Seq) -and $ev.LatestSeq -eq $ev.Events[-1].Seq)
  $after = Api GET "/LabelMaker/Events?After=$($ev.LatestSeq)"
  Check 'Asking after the latest number returns nothing' (@($after.Events).Count -eq 0)

  Write-Host '-- the album list and the review actions'
  $list = Api GET '/LabelMaker/Albums?State=Ready'
  Check 'The album list shows the album with artist and album' ($list.Total -eq 1 -and $list.Items[0].Artist -eq 'Green Day' -and $list.Items[0].Album -eq 'Insomniac') ($list | ConvertTo-Json -Compress -Depth 4)
  $id = $list.Items[0].Id
  $detail = Api GET "/LabelMaker/Albums/$id"
  Check 'The album detail lists both files and a candidate edition' (@($detail.FileList).Count -eq 2 -and @($detail.Candidates).Count -ge 1)
  $prev = Api POST "/LabelMaker/Albums/$id/Preview" @{ ReleaseId = $list.Items[0].BestReleaseId }
  Check 'A preview says what would happen and changes nothing' ($prev.Success -and $prev.CanApply -and @($prev.Files).Count -eq 2 -and (Count-Mp3 $library) -eq 0)
  $applied = Api POST "/LabelMaker/Albums/$id/Apply" @{ ReleaseId = $list.Items[0].BestReleaseId }
  Check 'Approving files the album (even in dry run mode)' ($applied.Success -and (Count-Mp3 $library) -eq 2 -and (Count-Mp3 $inbox) -eq 0) ([string]$applied.Message)
  $filed = @(Get-ChildItem $library -Recurse -Filter *.mp3)[0]
  $t = [TagLib.File]::Create($filed.FullName); $mb = $t.Tag.MusicBrainzReleaseId; $t.Dispose()
  Check 'The filed files carry the MusicBrainz release id' ([bool]$mb)
  $undo = Api POST "/LabelMaker/Albums/$id/Undo"
  Check 'Undo puts the album back in the inbox' ($undo.Success -and (Count-Mp3 $inbox) -eq 2 -and (Count-Mp3 $library) -eq 0) ([string]$undo.Message)
  $disc = Api POST "/LabelMaker/Albums/$id/Discard"
  Check 'Discard moves the album to _Trash and nothing is deleted' ($disc.Success -and (Count-Mp3 $inbox) -eq 0 -and @(Get-ChildItem (Join-Path $inbox '_Trash') -Recurse -Filter *.mp3).Count -eq 2) ([string]$disc.Message)
  $rest = Api POST "/LabelMaker/Albums/$id/Restore"
  Check 'Restore brings it back' ($rest.Success -and (Count-Mp3 $inbox) -eq 2) ([string]$rest.Message)
  $skip = Api POST "/LabelMaker/Albums/$id/Skip"
  Check 'Skip holds the album' ($skip.Success -and (Count-Of 'Skipped') -eq 1)
  $null = Api POST "/LabelMaker/Albums/$id/Recheck"

  Write-Host '-- pause'
  $null = Api POST '/LabelMaker/Pause' @{ Paused = $true }
  Check 'Pause shows in the status' ((Status).Paused -eq $true)
  $null = Api POST '/LabelMaker/Pause' @{ Paused = $false }
  Check 'Resume shows in the status' ((Status).Paused -eq $false)

  Write-Host '-- a broken lane is reported once and recovered'
  $null = ReadWs 1
  [IO.Directory]::Delete($library, $true)
  Run-Pass; $null = WaitUntil { (@((Status).Lanes[0].Problems).Count -ge 1) } 30 1
  $prob = @((Status).Lanes[0].Problems)
  Check 'The status names the problem' ($prob.Count -ge 1 -and $prob[0].Kind -eq 'LibraryUnreachable' -and $prob[0].Blocking -eq $true) (($prob | ConvertTo-Json -Compress))
  $m2 = ReadWs 4
  Check 'A LaneProblem alert was pushed with no path' (@($m2 | Where-Object { $_.MessageType -eq 'LabelMaker.LaneProblem' }).Count -eq 1 -and ((($m2 | ConvertTo-Json -Depth 8)) -notmatch [regex]::Escape($tempRoot)))
  New-Item -ItemType Directory -Force $library | Out-Null
  Run-Pass; $null = WaitUntil { @((Status).Lanes[0].Problems).Count -eq 0 } 30 1
  $m3 = ReadWs 4
  Check 'A LaneRecovered alert followed' (@($m3 | Where-Object { $_.MessageType -eq 'LabelMaker.LaneRecovered' }).Count -eq 1)

  Write-Host '-- real mode: a new album is filed by itself'
  Save-Config (Lane-Obj) $false
  [IO.Directory]::Delete((Join-Path $inbox 'Albums'), $true)
  Add-Album $inbox 'Green Day'
  $null = ReadWs 1
  Run-Pass; Start-Sleep 12; Run-Pass
  $okDone = WaitUntil { (Count-Mp3 $library) -eq 2 } 120
  Check 'With dry run off the album was filed by itself' $okDone ((Status).Counts | ConvertTo-Json -Compress)
  $m4 = ReadWs 6
  Check 'An Applied alert was pushed' (@($m4 | Where-Object { $_.MessageType -eq 'LabelMaker.AutoApplied' -and $_.Data.Mode -eq 'Applied' }).Count -eq 1) (($m4 | ForEach-Object { $_.MessageType }) -join ',')
  Check 'The inbox was tidied' ((Count-Mp3 $inbox) -eq 0)

  Write-Host '-- the subscription again: heartbeat, replay, and the token never leaks'
  $heartbeats = New-Object System.Collections.ArrayList
  [void]$heartbeats.AddRange(@(Read-App $app 38 | Where-Object { @($_.Events).Count -eq 0 -and -not $_.Error }))
  Check 'A quiet subscription gets an empty heartbeat (within about 30 seconds)' ($heartbeats.Count -ge 1 -and $heartbeats[0].LatestSeq -ge 1) ($heartbeats.Count.ToString() + ' heartbeats')
  $latest = (Api GET '/LabelMaker/Events').LatestSeq
  $replay = New-AppSocket $script:Token 'lm-app-replay'
  Send-App $replay (Start-Message '0' $script:Token)
  $replayed = @(Read-App $replay 8)
  $replaySeqs = @($replayed | ForEach-Object { @($_.Events) } | Where-Object { $_ } | ForEach-Object { [long]$_.Seq })
  Check 'Subscribing with afterSeq 0 replays the log oldest first' ($replaySeqs.Count -ge 3 -and (($replaySeqs -join ',') -eq ((@($replaySeqs | Sort-Object)) -join ',')) -and $replayed[0].Truncated -eq $false -and $replayed[0].LatestSeq -eq $latest) ($replaySeqs -join ',')
  $caught = New-AppSocket $script:Token 'lm-app-caught'
  Send-App $caught (Start-Message ([string]$latest) $script:Token)
  $caughtUp = @(Read-App $caught 8)
  Check 'Subscribing with the latest number replays nothing' (@($caughtUp).Count -ge 1 -and @($caughtUp[0].Events).Count -eq 0 -and $caughtUp[0].LatestSeq -eq $latest)
  # No token the test used may appear in anything the plugin or Emby wrote down.
  $secrets = @($script:Token, $userAuth.AccessToken, $junkToken)
  $written = @(Get-ChildItem $rigData -Recurse -Include *.txt, *.log, *.json, *.xml -File -ErrorAction SilentlyContinue)
  $leaks = @()
  foreach ($f in $written) { foreach ($secret in $secrets) { try { if ((Select-String -Path $f.FullName -Pattern $secret -SimpleMatch -Quiet -ErrorAction Stop)) { $leaks += $f.Name } } catch {} } }
  Check 'No access token appears in any log or file the plugin or Emby wrote' ($leaks.Count -eq 0) (($leaks | Select-Object -Unique) -join ', ')

  Write-Host '-- alerts can be switched off'
  $cfg = Api GET "/Plugins/$pluginId/Configuration"; $cfg.SendPhoneAlerts = $false
  Api POST "/Plugins/$pluginId/Configuration" $cfg | Out-Null
  $seqBefore = (Api GET '/LabelMaker/Events').LatestSeq
  [IO.Directory]::Delete($library, $true); Run-Pass; Start-Sleep 3
  Check 'With the master switch off nothing is sent or logged' ((Api GET '/LabelMaker/Events').LatestSeq -eq $seqBefore)
}
catch {
  Check 'The test run finished without an unexpected error' $false ($_.Exception.Message + ' @ line ' + $_.InvocationInfo.ScriptLineNumber)
}
finally {
  Write-Host '-- cleanup'
  try { if ($ws) { $ws.Dispose() } } catch {}
  foreach ($s in $script:appSockets) { try { $s.Ws.Dispose() } catch {} }
  if ($KeepRunning -and $script:rigProcessId -gt 0) {
    @{ Pid = $script:rigProcessId; Port = $Port; User = 'lm-rig-admin'; Password = $script:Password; Root = $tempRoot } | ConvertTo-Json | Set-Content -Encoding utf8 (Join-Path ([IO.Path]::GetTempPath()) 'lm-live-instance.json')
    Write-Host ("  private instance left running on port $Port (user lm-rig-admin, password " + $script:Password + "); stop it with tools\Stop-LabelMakerTestInstance.ps1")
  }
  else {
    Stop-Rig
    for ($i = 0; $i -lt 5 -and (Test-Path $tempRoot); $i++) { try { [IO.Directory]::Delete($tempRoot, $true) } catch { Start-Sleep 2 } }
    if (Test-Path $tempRoot) { Write-Host "  could not remove $tempRoot; delete it by hand" -ForegroundColor Yellow } else { Write-Host '  private instance stopped and its folder deleted' }
  }
  $failed = @($results | Where-Object { -not $_.Ok })
  Write-Host ''
  Write-Host ("== {0} checks, {1} failed" -f $results.Count, $failed.Count)
  if ($failed.Count -gt 0) { exit 1 } else { exit 0 }
}
