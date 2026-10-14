<#
Live regression test for the Label Maker plugin on the local test Emby server (see CLAUDE.md for the server).
It deploys the built DLL, runs the whole flow with real tagged files and the real MusicBrainz, watches the phone alerts through a throw-away
administrator websocket, and then removes everything it made and puts the server back the way it found it.

  powershell -ExecutionPolicy Bypass -File tools\Test-LabelMakerPlugin.ps1 [-DllPath <path>] [-KeepRunning]

Needs: the admin API key in %USERPROFILE%\.emby-spike-key (never printed), network access to musicbrainz.org.
Never touches the server's existing users: it creates one throw-away administrator and deletes it. Exit code 0 only when every check passed.
#>
param(
  [string]$DllPath = '',
  [string]$ServerRoot = (Join-Path $env:USERPROFILE 'Downloads\embyserver-win-x64-4.10.1.0'),
  [string]$WorkRoot = 'C:\claude',
  [switch]$KeepRunning,
  [switch]$Force
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
[Environment]::CurrentDirectory = $repo
if ($DllPath -eq '') { $DllPath = Join-Path $repo 'src\LabelMaker\bin\Release\net6.0\LabelMaker.dll' }
if (-not (Test-Path $DllPath)) { throw "Build first: $DllPath not found." }

$base = 'http://localhost:8096/emby'
$pluginId = 'a29bf822-41ab-4e14-a814-a8d386e7f745'
$pluginsDir = Join-Path $ServerRoot 'programdata\plugins'
$configDir = Join-Path $pluginsDir 'configurations'
$key = (Get-Content (Join-Path $env:USERPROFILE '.emby-spike-key')).Trim()
$H = @{ 'X-Emby-Token' = $key }
$results = New-Object System.Collections.ArrayList
$work = Join-Path $WorkRoot ('lm-live-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$inbox = Join-Path $work 'inbox'
$library = Join-Path $work 'library'
$wasRunning = [bool](Get-Process | Where-Object ProcessName -like 'Emby*')
$user = $null
$ws = $null
$script:pending = $null
$script:buf = New-Object byte[] 65536   # one buffer for the whole run: a read that is still waiting writes into it

function Check([string]$name, [bool]$ok, [string]$detail = '') {
  [void]$results.Add([pscustomobject]@{ Name = $name; Ok = $ok; Detail = $detail })
  if ($ok) { Write-Host ("  ok   " + $name) } else { Write-Host ("  FAIL " + $name + "  " + $detail) -ForegroundColor Red }
}
function Api([string]$method, [string]$path, $body = $null, $headers = $H) {
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
function Stop-Emby { Get-Process | Where-Object ProcessName -like 'Emby*' | Stop-Process -Force; Start-Sleep 3 }
function Start-Emby {
  Start-Process (Join-Path $ServerRoot 'system\EmbyServer.exe') -ArgumentList ('-programdata "' + (Join-Path $ServerRoot 'programdata') + '"') -WindowStyle Minimized
  $up = WaitUntil { try { $null = Invoke-RestMethod -Headers $H "$base/System/Info"; $true } catch { $false } } 150 3
  if (-not $up) { throw 'Emby did not come up.' }
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

  if ($wasRunning) {
    $sessions = @(Invoke-RestMethod -Headers $H "$base/Sessions" | Where-Object { $_.UserName -and $_.UserName -notlike 'lm-live-*' })
    $busy = @($sessions | Where-Object { $_.LastActivityDate -and ([datetime]$_.LastActivityDate) -gt (Get-Date).AddMinutes(-10).ToUniversalTime() })
    if ($busy.Count -gt 0 -and -not $Force) { throw 'Someone used the test server in the last 10 minutes. Run again later, or use -Force.' }
  }
  $hadDll = Test-Path (Join-Path $pluginsDir 'LabelMaker.dll')
  if ($hadDll) { throw 'LabelMaker.dll is already in the test server plugins folder. Remove it first (this script leaves the server as it found it).' }

  Write-Host '-- deploy'
  Stop-Emby
  Copy-Item $DllPath (Join-Path $pluginsDir 'LabelMaker.dll')
  Start-Emby
  Start-Sleep 25   # the plugin waits 20 seconds before its first check

  Write-Host '-- routes'
  $info = Api GET '/LabelMaker/Info'
  Check 'Info has a version and ApiLevel 1' ($info.PluginVersion -and $info.ApiLevel -eq 1) ($info | ConvertTo-Json -Compress)
  Check 'Info lists the features' ((@($info.Features) -contains 'events') -and (@($info.Features) -contains 'catalog') -and (@($info.Features) -contains 'status'))
  $cat = Api GET '/LabelMaker/Notifications/Catalog'
  Check 'Catalog has the five alert rules' (@($cat.Events).Count -eq 5 -and $cat.Service.ChannelId -eq 'music-ingest') ((@($cat.Events) | ForEach-Object { $_.Id }) -join ',')
  $st = Status
  Check 'Status has the counts, lanes, service and LatestSeq' ($st.Counts -and $st.Service.MusicBrainz -eq 'Ok' -and $st.LatestSeq -ne $null -and $st.MatchingVersion)
  $page = Invoke-WebRequest -UseBasicParsing -Headers $H "$base/web/configurationpage?name=LabelMakerConfiguration"
  Check 'The settings page is served' ($page.Content -match 'Label Maker')
  # The exact script and markup the plugin serves, loaded in a real browser: a script error here is the "menu item does nothing" failure.
  $served = Join-Path $env:TEMP ('lm-served-' + [Guid]::NewGuid().ToString('N').Substring(0, 6))
  New-Item -ItemType Directory -Force $served | Out-Null
  Invoke-WebRequest -UseBasicParsing -Headers $H "$base/web/configurationpage?name=LabelMakerConfigurationjs" -OutFile (Join-Path $served 'page.js')
  Invoke-WebRequest -UseBasicParsing -Headers $H "$base/web/configurationpage?name=LabelMakerConfiguration" -OutFile (Join-Path $served 'page.html')
  $pageOut = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Check-PageScript.ps1') -ScriptPath (Join-Path $served 'page.js') -HtmlPath (Join-Path $served 'page.html') 2>&1 | Out-String
  Check 'The page script the plugin serves loads in a real browser without errors' ($LASTEXITCODE -eq 0) $pageOut.Trim()
  Remove-Item -Recurse -Force $served -ErrorAction SilentlyContinue
  $anon = $false; try { Invoke-RestMethod "$base/LabelMaker/Status" | Out-Null } catch { $anon = $true }
  Check 'A request with no token is refused' $anon

  Write-Host '-- settings are checked when saved'
  $bad = TryApi POST "/Plugins/$pluginId/Configuration" ((Api GET "/Plugins/$pluginId/Configuration") | ForEach-Object { $_.Lanes = @(@{ Id = 'x'; Name = 'X'; InboxPath = 'relative\path'; LibraryPath = 'E:\lib'; AutoApply = $true }); $_ })
  Check 'A lane with a relative path is refused with a plain message' ((-not $bad.Ok) -and ($bad.Data -match 'full path' -or $bad.Message -match '400|500')) ([string]$bad.Data)
  $badScore = TryApi POST "/Plugins/$pluginId/Configuration" ((Api GET "/Plugins/$pluginId/Configuration") | ForEach-Object { $_.AutoThreshold = 0.2; $_ })
  Check 'A threshold out of range is refused' (-not $badScore.Ok)

  Write-Host '-- throw-away administrator websocket'
  $pw = [Guid]::NewGuid().ToString('N') + 'Aa1!'
  $user = Api POST '/Users/New' @{ Name = 'lm-live-admin' }
  Api POST "/Users/$($user.Id)/Password" @{ NewPw = $pw; CurrentPw = '' } | Out-Null
  $u = Api GET "/Users/$($user.Id)"; $p = $u.Policy; $p.IsAdministrator = $true
  Api POST "/Users/$($user.Id)/Policy" $p | Out-Null
  $auth = Api POST '/Users/AuthenticateByName' @{ Username = 'lm-live-admin'; Pw = $pw } @{ 'X-Emby-Authorization' = 'MediaBrowser Client="lm-live", Device="probe", DeviceId="lm-live-1", Version="1"' }
  $ws = New-Object System.Net.WebSockets.ClientWebSocket
  $ws.ConnectAsync([Uri]("ws://localhost:8096/embywebsocket?api_key=" + $auth.AccessToken + "&deviceId=lm-live-1"), [Threading.CancellationToken]::None).Wait()
  Check 'The websocket connected' ($ws.State -eq 'Open')

  Write-Host '-- a lane, a dry run, and the alerts'
  New-Item -ItemType Directory -Force $inbox, $library | Out-Null
  Save-Config (Lane-Obj) $true
  $pre = Api GET '/LabelMaker/Preflight'
  $laneResult = @($pre.Lanes)[0]
  Check 'Preflight passes for the new lane' ($laneResult.CanRun -eq $true) (($laneResult.Checks | Where-Object { $_.Level -eq 'Error' } | ForEach-Object { $_.Message }) -join ' | ')
  Add-Album $inbox 'Green Day'
  Run-Pass; Start-Sleep 12; Run-Pass
  $ok = WaitUntil { (Count-Of 'Ready') -ge 1 } 120
  Check 'The album became Ready (dry run) after matching on MusicBrainz' $ok ((Status).Counts | ConvertTo-Json -Compress)
  $msgs = ReadWs 6
  Check 'A WouldApply alert was pushed to the administrator websocket' (@($msgs | Where-Object { $_.MessageType -eq 'LabelMaker.AutoApplied' -and $_.Data.Mode -eq 'WouldApply' }).Count -eq 1) (($msgs | ForEach-Object { $_.MessageType }) -join ',')
  Check 'Every alert carries the common fields' (@($msgs | Where-Object { $_.Data.ApiLevel -eq 1 -and $_.Data.Seq -gt 0 -and $_.Data.EventId -and $_.Data.ServerTimeUtc -and $_.Data.Event }).Count -eq @($msgs).Count -and @($msgs).Count -gt 0)
  $raw = ($msgs | ConvertTo-Json -Depth 8)
  Check 'No alert carries a folder path' (($raw -notmatch [regex]::Escape($work)) -and ($raw -notmatch 'lm-live-'))
  Check 'Dry run changed nothing in the library' ((Count-Of 'Done') -eq 0 -and (Count-Mp3 $library) -eq 0 -and (Count-Mp3 $inbox) -eq 2)

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
  Remove-Item -Recurse -Force $library
  Run-Pass; $null = WaitUntil { (@((Status).Lanes[0].Problems).Count -ge 1) } 30 1
  $prob = @((Status).Lanes[0].Problems)
  Check 'The status names the problem' ($prob.Count -ge 1 -and $prob[0].Kind -eq 'LibraryUnreachable' -and $prob[0].Blocking -eq $true) (($prob | ConvertTo-Json -Compress))
  $m2 = ReadWs 4
  Check 'A LaneProblem alert was pushed with no path' (@($m2 | Where-Object { $_.MessageType -eq 'LabelMaker.LaneProblem' }).Count -eq 1 -and ((($m2 | ConvertTo-Json -Depth 8)) -notmatch [regex]::Escape($work)))
  New-Item -ItemType Directory -Force $library | Out-Null
  Run-Pass; $null = WaitUntil { @((Status).Lanes[0].Problems).Count -eq 0 } 30 1
  $m3 = ReadWs 4
  Check 'A LaneRecovered alert followed' (@($m3 | Where-Object { $_.MessageType -eq 'LabelMaker.LaneRecovered' }).Count -eq 1)

  Write-Host '-- real mode: a new album is filed by itself'
  Save-Config (Lane-Obj) $false
  Remove-Item -Recurse -Force (Join-Path $inbox 'Albums') -ErrorAction SilentlyContinue
  Add-Album $inbox 'Green Day'
  $null = ReadWs 1
  Run-Pass; Start-Sleep 12; Run-Pass
  $okDone = WaitUntil { (Count-Mp3 $library) -eq 2 } 120
  Check 'With dry run off the album was filed by itself' $okDone ((Status).Counts | ConvertTo-Json -Compress)
  $m4 = ReadWs 6
  Check 'An Applied alert was pushed' (@($m4 | Where-Object { $_.MessageType -eq 'LabelMaker.AutoApplied' -and $_.Data.Mode -eq 'Applied' }).Count -eq 1) (($m4 | ForEach-Object { $_.MessageType }) -join ',')
  Check 'The inbox was tidied' ((Count-Mp3 $inbox) -eq 0)

  Write-Host '-- alerts can be switched off'
  $cfg = Api GET "/Plugins/$pluginId/Configuration"; $cfg.SendPhoneAlerts = $false
  Api POST "/Plugins/$pluginId/Configuration" $cfg | Out-Null
  $seqBefore = (Api GET '/LabelMaker/Events').LatestSeq
  Remove-Item -Recurse -Force $library; Run-Pass; Start-Sleep 3
  Check 'With the master switch off nothing is sent or logged' ((Api GET '/LabelMaker/Events').LatestSeq -eq $seqBefore)
}
catch {
  Check 'The test run finished without an unexpected error' $false ($_.Exception.Message + ' @ line ' + $_.InvocationInfo.ScriptLineNumber)
}
finally {
  Write-Host '-- cleanup'
  try { if ($ws) { $ws.Dispose() } } catch {}
  try { if ($user) { Invoke-RestMethod -Method Delete -Headers $H "$base/Users/$($user.Id)" | Out-Null; Write-Host '  throw-away user deleted' } } catch { Write-Host ('  could not delete the throw-away user: ' + $_.Exception.Message) -ForegroundColor Yellow }
  if (-not $KeepRunning) {
    Stop-Emby
    foreach ($f in @((Join-Path $pluginsDir 'LabelMaker.dll'), (Join-Path $configDir 'LabelMaker.xml'))) { if (Test-Path $f) { [IO.File]::Delete($f) } }
    $data = Join-Path $configDir 'LabelMaker.Data'
    if (Test-Path $data) { Remove-Item -Recurse -Force $data }
    if ($wasRunning) { Start-Emby; Write-Host '  server restarted (it was running before)' } else { Write-Host '  server left stopped (it was stopped before)' }
  }
  if (Test-Path $work) { Remove-Item -Recurse -Force $work }
  $failed = @($results | Where-Object { -not $_.Ok })
  Write-Host ''
  Write-Host ("== {0} checks, {1} failed" -f $results.Count, $failed.Count)
  if ($failed.Count -gt 0) { exit 1 } else { exit 0 }
}
