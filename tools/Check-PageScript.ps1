<#
Loads the plugin page's JavaScript in a real (headless) browser and fails on any error. A script with a syntax error makes the
page silently refuse to open in Emby (the menu item "does nothing"), and the engine tests cannot see that, so this runs before
every live test and before every release.

  powershell -ExecutionPolicy Bypass -File tools\Check-PageScript.ps1 [-ScriptPath <path to the .js>]

It needs Google Chrome (or Microsoft Edge where its headless mode is not blocked by policy). The page markup is loaded too, so a script that looks for an element the page does not have also fails. Exit code 0 only when the script parses, defines its page class and builds one over the real markup.
#>
param(
  [string]$ScriptPath = '',
  [string]$HtmlPath = ''
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if ($ScriptPath -eq '') { $ScriptPath = Join-Path $repo 'src\LabelMaker\LabelMaker.LabelMakerConfiguration.js' }
if ($HtmlPath -eq '') { $HtmlPath = Join-Path $repo 'src\LabelMaker\LabelMaker.LabelMakerConfiguration.html' }
if (-not (Test-Path $ScriptPath)) { throw "Not found: $ScriptPath" }
if (-not (Test-Path $HtmlPath)) { throw "Not found: $HtmlPath" }

$browser = @(
  'C:\Program Files\Google\Chrome\Application\chrome.exe',
  'C:\Program Files (x86)\Google\Chrome\Application\chrome.exe',
  'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe',
  'C:\Program Files\Microsoft\Edge\Application\msedge.exe'
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $browser) { throw 'Neither Microsoft Edge nor Google Chrome was found.' }

$work = Join-Path ([IO.Path]::GetTempPath()) ('lm-pagecheck-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force $work | Out-Null
try {
  Copy-Item $ScriptPath (Join-Path $work 'page.js')
  # The page script is an AMD module: define([...], function (BaseView, loading, toast) { return class ... }).
  # Stand-ins for Emby's modules are enough to prove it parses, runs, and returns a class that can be built.
  $html = @'
<!doctype html><html><body><pre id="r">pending</pre><div id="host">@@PAGE@@</div>
<script>
window.__result = 'no define() call was made by the page script';
window.onerror = function (message, source, line, col) { window.__result = 'ERROR: ' + message + ' (line ' + line + ', column ' + col + ')'; };
window.define = function (deps, factory) {
  try {
    function BaseView(view, params) { this.view = view; }
    BaseView.prototype.onResume = function () {}; BaseView.prototype.onPause = function () {}; BaseView.prototype.destroy = function () {};
    var Page = factory(BaseView, { show: function () {}, hide: function () {} }, function () {});
    if (typeof Page !== 'function') { window.__result = 'ERROR: the module did not return a class'; return; }
    var root = document.getElementById('host');
    var instance = new Page(root, {});
    window.__result = 'OK';
  } catch (e) { window.__result = 'ERROR: ' + e.message; }
};
</script>
<script src="page.js"></script>
<script>document.getElementById('r').textContent = window.__result;</script>
</body></html>
'@
  $html = $html.Replace('@@PAGE@@', [IO.File]::ReadAllText($HtmlPath))
  [IO.File]::WriteAllText((Join-Path $work 'check.html'), $html)
  $url = ([Uri](Join-Path $work 'check.html')).AbsoluteUri
  $profileDir = Join-Path $work 'profile'
  $outFile = Join-Path $work 'out.txt'
  $errFile = Join-Path $work 'err.txt'
  $argList = @('--headless=new', '--disable-gpu', '--no-first-run', "--user-data-dir=`"$profileDir`"", '--allow-file-access-from-files', '--virtual-time-budget=3000', '--dump-dom', $url)
  $null = Start-Process -FilePath $browser -ArgumentList $argList -RedirectStandardOutput $outFile -RedirectStandardError $errFile -Wait -PassThru -WindowStyle Hidden
  $dom = if (Test-Path $outFile) { Get-Content $outFile -Raw } else { '' }
  if ($dom -match '<pre id="r">([^<]*)</pre>') { $result = $Matches[1].Trim() } else { $result = 'ERROR: the browser gave no answer' }
  if ($result -eq 'OK') { Write-Host "Page script OK ($ScriptPath)"; exit 0 }
  Write-Host "Page script FAILED: $result" -ForegroundColor Red
  exit 1
}
finally {
  Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
