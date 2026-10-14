$root=Join-Path $env:USERPROFILE 'Downloads\embyserver-win-x64-4.10.1.0'
$sp=Join-Path $env:TEMP 'label-maker-spike'; New-Item -ItemType Directory -Force $sp | Out-Null
$b="$sp\probe\bin\Release\net6.0"
Get-Process | ? ProcessName -like 'Emby*' | Stop-Process -Force
Start-Sleep 3
Remove-Item "$root\programdata\plugins\TagLibSharp.dll" -ErrorAction SilentlyContinue
Copy-Item "$b\LabelMakerProbe.dll" "$root\programdata\plugins\" -Force
Start-Process "$root\system\EmbyServer.exe" -ArgumentList "-programdata `"$root\programdata`"" -WindowStyle Minimized
Start-Sleep 40
$k=(Get-Content $env:USERPROFILE\.emby-spike-key).Trim()
try { Invoke-RestMethod -Headers @{'X-Emby-Token'=$k} "http://localhost:8096/emby/LabelMakerProbe/Run?Dir=$sp\spike\fx" | ConvertTo-Json -Depth 4 } catch { $_.Exception.Message }
