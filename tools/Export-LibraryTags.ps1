<#
.SYNOPSIS
  Exports the tags (no audio) of a random sample of albums from a music library to one JSON file, for Label Maker's
  calibration. Run it on the machine that has the library. Read-only: it never changes a music file.

.DESCRIPTION
  Every folder that directly holds audio files counts as one album. The file holds, per track: title, artist, album artist,
  album, track and disc numbers and totals, year, length, and the MusicBrainz release id when the file has one (the library's
  correct, Picard-tagged albums usually do, and that id is the answer Label Maker's matcher is checked against).

  Privacy: the output has artist, album and track names from your library and nothing else. It has no file contents, no
  absolute paths (only the folder path under the library root), no server or share names, and no account names.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File Export-LibraryTags.ps1 -Library "\\server\Music" -MaxAlbums 250
#>
param(
    [Parameter(Mandatory = $true)][string]$Library,
    [string]$Out = (Join-Path (Get-Location) 'library-tags.json'),
    [int]$MaxAlbums = 250,
    [int]$Seed = 1,
    [string]$TagLibDll = ''
)

$ErrorActionPreference = 'Stop'
# $PSScriptRoot is empty inside parameter defaults in Windows PowerShell 5.1, so the default is set here.
if (-not $TagLibDll) { $TagLibDll = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'TagLibSharp.dll' }
if (-not (Test-Path -LiteralPath $Library -PathType Container)) { throw "The library folder was not found: $Library" }
if (-not (Test-Path -LiteralPath $TagLibDll)) { throw "TagLibSharp.dll was not found next to this script ($TagLibDll)." }
Add-Type -Path $TagLibDll

$extensions = @('.mp3', '.flac', '.m4a', '.ogg', '.opus')
$root = (Resolve-Path -LiteralPath $Library).Path.TrimEnd('\', '/')

Write-Host "Scanning $root ..."
$files = Get-ChildItem -LiteralPath $root -Recurse -File | Where-Object { $extensions -contains $_.Extension.ToLowerInvariant() }
$byFolder = $files | Group-Object DirectoryName
Write-Host ("Found {0} audio files in {1} folders." -f $files.Count, $byFolder.Count)

# A repeatable random sample: the same -Seed gives the same albums.
$sample = $byFolder | Get-Random -Count ([Math]::Min($MaxAlbums, $byFolder.Count)) -SetSeed $Seed

# ID3v2.3 separates several artists with '/', so TagLib splits 'AC/DC' into 'AC' and 'DC': put the name back together for those files.
function NameOf($names, $slashSplit) { $a = @($names); if ($a.Count -eq 0) { '' } elseif ($slashSplit) { $a -join '/' } else { [string]$a[0] } }

function Number($value) { if ($value -and [int]$value -gt 0) { [int]$value } else { $null } }

$albums = New-Object System.Collections.Generic.List[object]
$done = 0
foreach ($group in $sample) {
    $done++
    if ($done % 25 -eq 0) { Write-Host ("  {0} of {1} albums" -f $done, $sample.Count) }
    $tracks = New-Object System.Collections.Generic.List[object]
    foreach ($file in ($group.Group | Sort-Object Name)) {
        try {
            $tl = [TagLib.File]::Create($file.FullName)
            try {
                $t = $tl.Tag
                $id3 = $tl.GetTag([TagLib.TagTypes]::Id3v2, $false)
                $slashSplit = ($null -ne $id3) -and ($id3.Version -lt 4)
                $tracks.Add([ordered]@{
                    file         = $file.Name
                    title        = [string]$t.Title
                    artist       = NameOf $t.Performers $slashSplit
                    albumArtist  = NameOf $t.AlbumArtists $slashSplit
                    album        = [string]$t.Album
                    track        = Number $t.Track
                    trackCount   = Number $t.TrackCount
                    disc         = Number $t.Disc
                    discCount    = Number $t.DiscCount
                    year         = Number $t.Year
                    durationMs   = [int]$tl.Properties.Duration.TotalMilliseconds
                    releaseMbid  = [string]$t.MusicBrainzReleaseId
                })
            } finally { $tl.Dispose() }
        } catch {
            Write-Warning ("Skipped one unreadable file in '{0}': {1}" -f $group.Name.Substring($root.Length).TrimStart('\', '/'), $_.Exception.Message)
        }
    }
    if ($tracks.Count -gt 0) {
        $albums.Add([ordered]@{
            folder = $group.Name.Substring($root.Length).TrimStart('\', '/')
            tracks = $tracks
        })
    }
}

$withMbid = @($albums | Where-Object { @($_.tracks | Where-Object { $_.releaseMbid }).Count -gt 0 }).Count
$result = [ordered]@{
    exportedUtc = (Get-Date).ToUniversalTime().ToString('o')
    seed        = $Seed
    albums      = $albums
}
$json = $result | ConvertTo-Json -Depth 6
[System.IO.File]::WriteAllText($Out, $json, (New-Object System.Text.UTF8Encoding($false)))

Write-Host ''
Write-Host ("Wrote {0} albums ({1} tracks) to {2}" -f $albums.Count, (($albums | ForEach-Object { $_.tracks.Count } | Measure-Object -Sum).Sum), $Out)
Write-Host ("Albums with a MusicBrainz release id in the tags: {0} of {1}" -f $withMbid, $albums.Count)
if ($withMbid -lt ($albums.Count / 2)) {
    Write-Warning 'Fewer than half of the albums carry a MusicBrainz release id, so there is no reliable answer key for them.'
}
