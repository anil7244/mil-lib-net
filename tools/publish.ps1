# Builds the copy that goes to a unit.
#
# One self-contained executable, the data folder beside it, and the README
# somebody reads first. Written down rather than remembered, because the
# difference between a good handover and a bad one is usually something
# somebody forgot to do by hand.
#
#   D:\dotnet10\dotnet.exe --version      # 10.x expected
#   powershell -File tools\publish.ps1
#
# Add -Fresh to start the data folder from the seeded database rather than
# from whatever this machine has been testing with.

param(
    [string] $Dotnet   = 'D:\dotnet10\dotnet.exe',
    [string] $Into     = 'publish\staging',
    # Where the member photos and book covers live. The web application serves
    # them from public\storage (what the database paths resolve against), so a
    # shipped build fetches them from there — or from app\data, once somebody
    # has copied them in and made that the one place they live.
    [string] $Pictures = 'D:\XAMPP\htdocs\mil-lib-sqlite\public\storage',
    [switch] $Fresh
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$out  = Join-Path $root $Into

Write-Host ''
Write-Host 'Building the shipped copy' -ForegroundColor Cyan
Write-Host ('  from  ' + $root)
Write-Host ('  into  ' + $out)
Write-Host ''

$env:DOTNET_ROOT = Split-Path -Parent $Dotnet

# Everything that was there before goes. A staging folder that accumulates
# leftovers from earlier builds is how a stale DLL reaches a client.
if (Test-Path $out) {
    Get-ChildItem $out -Force |
        Where-Object { $_.Name -ne 'data' } |
        Remove-Item -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $out | Out-Null

# _IsPublishing turns on the single-file, self-contained settings in the
# csproj. They live there rather than here so this script cannot disagree with
# a build done from an IDE.
& $Dotnet publish (Join-Path $root 'src\MilLib.Desktop\MilLib.Desktop.csproj') `
    -c Release `
    -r win-x64 `
    -p:_IsPublishing=true `
    -o $out `
    --nologo

if ($LASTEXITCODE -ne 0) {
    throw 'The publish failed. Nothing was staged.'
}

# The data folder: the records, the crest, and nothing else.
$data = Join-Path $out 'data'

New-Item -ItemType Directory -Force -Path $data | Out-Null

$seed = Join-Path $root 'app\data\database.sqlite'
$live = Join-Path $data 'database.sqlite'

if ($Fresh -or -not (Test-Path $live)) {
    if (Test-Path $seed) {
        Copy-Item $seed $live -Force

        # The write-ahead log belongs to the database it was written beside.
        # Carried along with a copied file it is replayed over it, which is a
        # very quiet way to hand somebody the wrong records.
        Remove-Item ($live + '-wal'), ($live + '-shm') -Force -ErrorAction SilentlyContinue

        Write-Host '  data\database.sqlite   from app\data' -ForegroundColor DarkGray
    }
    else {
        Write-Warning 'No seed database found at app\data\database.sqlite.'
    }
}
else {
    Write-Host '  data\database.sqlite   left as it was (pass -Fresh to replace it)' -ForegroundColor DarkGray
}

$crest = Join-Path $root 'app\data\crest.png'

if (Test-Path $crest) {
    Copy-Item $crest (Join-Path $data 'crest.png') -Force
}

# The faces and the covers. They are resolved relative to the data folder at
# run time, so a build that omits them shows a blank where every photo and
# every cover should be. Each folder is taken from app\data if it is there
# (the settled home), otherwise from the web application's storage, so the
# handover carries them either way.
foreach ($kind in 'member-photos', 'covers') {
    $from = Join-Path $root ('app\data\' + $kind)

    if (-not (Test-Path $from)) {
        $from = Join-Path $Pictures $kind
    }

    if (Test-Path $from) {
        $dest = Join-Path $data $kind

        New-Item -ItemType Directory -Force -Path $dest | Out-Null
        Copy-Item (Join-Path $from '*') $dest -Recurse -Force

        $n = (Get-ChildItem $dest -Recurse -File).Count

        Write-Host ('  data\' + $kind + '   ' + $n + ' file(s)') -ForegroundColor DarkGray
    }
    else {
        Write-Warning ("No $kind found in app\data or under $Pictures - " +
                       'the shipped build will show blanks where these should be.')
    }
}

# Nothing from testing goes out.
Remove-Item (Join-Path $data 'errors.log') -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $data 'installation.json') -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $data 'backups') -Recurse -Force -ErrorAction SilentlyContinue

$readme = Join-Path $root 'docs\README-FIRST.txt'

if (Test-Path $readme) {
    Copy-Item $readme (Join-Path $out 'README-FIRST.txt') -Force
}

Write-Host ''

$exe = Join-Path $out 'Library Manager.exe'

if (-not (Test-Path $exe)) {
    throw 'The executable is not where it should be. Something about the publish is wrong.'
}

# What actually went out, and how big it is. A single-file build that has
# quietly become forty files is worth noticing here rather than on a client's
# machine.
$loose = Get-ChildItem $out -File | Where-Object { $_.Extension -in '.dll', '.json' }

Write-Host 'Staged' -ForegroundColor Green

Get-ChildItem $out -Recurse -File |
    Sort-Object Length -Descending |
    Select-Object -First 8 |
    ForEach-Object {
        $size = if ($_.Length -ge 1MB) { '{0,7:N1} MB' -f ($_.Length / 1MB) }
                else { '{0,7:N0} KB' -f ($_.Length / 1KB) }

        Write-Host ('  ' + $size + '  ' + $_.FullName.Substring($out.Length + 1))
    }

Write-Host ''
Write-Host ('  ' + (Get-ChildItem $out -Recurse -File).Count + ' files in all')

if ($loose.Count -gt 0) {
    Write-Warning ("$($loose.Count) loose .dll/.json beside the executable - " +
                   'the single-file build is not doing what it should.')
}

Write-Host ''
Write-Host 'Hand over the whole folder. Nothing is installed on the other machine.' -ForegroundColor Cyan
Write-Host ''
