param(
    [string]$OutputRoot,
    [string]$MapPackage
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$mapSource = Join-Path $repository 'LauncherGo.Services/EmbeddedMods/ServerMapMod'
$webSource = Join-Path $repository 'LauncherGo.ServerMapHost/WebRoot'

function Normalize-Text([string]$Text) { return $Text.Replace("`r`n", "`n").TrimEnd() }
function Assert-File([string]$Path) {
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Missing distribution file: $Path" }
}
function Assert-License([string]$Actual, [string]$Expected) {
    Assert-File $Actual
    if ((Normalize-Text ([IO.File]::ReadAllText($Actual))) -cne (Normalize-Text ([IO.File]::ReadAllText($Expected)))) {
        throw "Stale or changed license/notice: $Actual"
    }
}
function Assert-MapPackage([string]$Path) {
    Assert-File $Path
    $archive = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $Path))
    try {
        foreach ($name in @('ServerMap.dll', 'modinfo.json', 'LICENSE.txt', 'THIRD_PARTY_NOTICES.txt', 'VS-LiveMap-Revival-LICENSE.txt')) {
            $entry = $archive.GetEntry($name)
            if ($null -eq $entry -or $entry.Length -eq 0) { throw "Missing $name in $Path" }
            if ($name.EndsWith('.txt')) {
                $reader = [IO.StreamReader]::new($entry.Open())
                try {
                    if ((Normalize-Text $reader.ReadToEnd()) -cne (Normalize-Text ([IO.File]::ReadAllText((Join-Path $mapSource $name))))) {
                        throw "Stale or changed $name in $Path"
                    }
                } finally { $reader.Dispose() }
            }
        }
        foreach ($entry in $archive.Entries) {
            if ($entry.FullName.EndsWith('.dll', [StringComparison]::OrdinalIgnoreCase) -and $entry.FullName -ne 'ServerMap.dll') {
                throw "Unexpected redistributed dependency in $Path : $($entry.FullName)"
            }
        }
    } finally { $archive.Dispose() }
    Write-Output "Verified map ZIP licenses: $Path"
}

if (!$OutputRoot -and !$MapPackage) { throw 'Specify OutputRoot or MapPackage.' }
if ($OutputRoot) {
    foreach ($mod in @('launchergoauth', 'launchergoredirect', 'launchergoserverbridge')) {
        Assert-License (Join-Path $OutputRoot "EmbeddedMods/$mod/LICENSE.txt") (Join-Path $repository 'LICENSE')
    }
    $mapOutput = Join-Path $OutputRoot 'EmbeddedMods/servermap'
    Assert-MapPackage (Join-Path $mapOutput 'servermap.zip')
    if (Test-Path -LiteralPath (Join-Path $mapOutput 'ServerMap.dll')) {
        foreach ($file in @('LICENSE.txt', 'THIRD_PARTY_NOTICES.txt', 'VS-LiveMap-Revival-LICENSE.txt')) {
            Assert-License (Join-Path $mapOutput $file) (Join-Path $mapSource $file)
        }
    }
    foreach ($file in @('LICENSE.txt', 'THIRD_PARTY_NOTICES.txt', 'vendor/leaflet/LICENSE.txt', 'vendor/WebCartographer-LICENSE.txt', 'vendor/VS-LiveMap-Revival-LICENSE.txt')) {
        Assert-License (Join-Path $OutputRoot "WebRoot/$file") (Join-Path $webSource $file)
    }
    foreach ($file in @('index.html', 'vendor/leaflet/leaflet.js', 'vendor/leaflet/leaflet.css', 'vendor/webcartographer-route.js')) {
        Assert-File (Join-Path $OutputRoot "WebRoot/$file")
    }
    $spawnIcon = Join-Path $OutputRoot 'WebRoot/assets/icons/spawn.png'
    Assert-File $spawnIcon
    if ((Get-FileHash -LiteralPath $spawnIcon).Hash -ne (Get-FileHash -LiteralPath (Join-Path $webSource 'assets/icons/spawn.png')).Hash) {
        throw "Stale or changed spawn icon: $spawnIcon"
    }
    if (Test-Path -LiteralPath (Join-Path $OutputRoot 'WebRoot/assets/icons/temporal-gear.png')) {
        throw "Retired spawn icon must not be redistributed: $OutputRoot"
    }
    Write-Output "Verified embedded mod and web licenses: $OutputRoot"
}
if ($MapPackage) { Assert-MapPackage $MapPackage }
