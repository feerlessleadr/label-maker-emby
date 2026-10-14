<#
Stops the private Emby instance that `Test-LabelMakerPlugin.ps1 -KeepRunning` left running, and deletes its folder.
It only touches the process and folder recorded by that run; the shared test server and every other Emby are left alone.

  powershell -ExecutionPolicy Bypass -File tools\Stop-LabelMakerTestInstance.ps1
#>
$ErrorActionPreference = 'Continue'
$record = Join-Path ([IO.Path]::GetTempPath()) 'lm-live-instance.json'
if (-not (Test-Path $record)) { Write-Host 'No private test instance is recorded.'; exit 0 }
$info = Get-Content $record -Raw | ConvertFrom-Json
$system = Join-Path $info.Root 'system'
Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($system, [StringComparison]::OrdinalIgnoreCase) } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Start-Sleep 3
for ($i = 0; $i -lt 5 -and (Test-Path $info.Root); $i++) { try { [IO.Directory]::Delete($info.Root, $true) } catch { Start-Sleep 2 } }
[IO.File]::Delete($record)
if (Test-Path $info.Root) { Write-Host "Could not remove $($info.Root); delete it by hand." } else { Write-Host 'Private test instance stopped and its folder deleted.' }
