param([string]$OutputRoot)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$licenseRoot = Join-Path $repository 'THIRD-PARTY-LICENSES'
$packages = @(Get-Content -LiteralPath (Join-Path $licenseRoot 'nuget-packages.json') -Raw | ConvertFrom-Json)
$upstream = @(Get-Content -LiteralPath (Join-Path $licenseRoot 'upstream-sources.json') -Raw | ConvertFrom-Json)
function Require-File([string]$Path) {
    if (!(Test-Path -LiteralPath $Path -PathType Leaf) -or (Get-Item -LiteralPath $Path).Length -eq 0) { throw "Missing license file: $Path" }
}
foreach ($item in $upstream) { Require-File (Join-Path $repository $item.file) }
foreach ($package in $packages) {
    $hasLicense = @($package.documents | Where-Object { $_.source -match '(^|/)(license|licence)(\.|$)' }).Count -gt 0
    if (!$hasLicense -and !($upstream | Where-Object { $_.packages -contains $package.package })) { throw "No full component license: $($package.package)" }
    foreach ($document in $package.documents) {
        $path = Join-Path $licenseRoot $document.file
        Require-File $path
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $document.sha256) { throw "Changed original package notice: $path" }
    }
}
$assetsPath = Join-Path $repository 'LauncherGo.App/obj/project.assets.json'
if (Test-Path -LiteralPath $assetsPath) {
    $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json -AsHashtable
    foreach ($item in $assets.libraries.GetEnumerator()) {
        if ($item.Value.type -eq 'package' -and $item.Key -notlike 'AvaloniaUI.DiagnosticsSupport/*' -and $packages.package -notcontains $item.Key) { throw "License inventory needs updating: $($item.Key)" }
    }
}
if ($OutputRoot) {
    foreach ($source in Get-ChildItem -LiteralPath $licenseRoot -File -Recurse) {
        $relative = [IO.Path]::GetRelativePath($repository, $source.FullName)
        $destination = Join-Path $OutputRoot $relative
        Require-File $destination
        if ($relative -like 'THIRD-PARTY-LICENSES*packages*') {
            if ((Get-FileHash -LiteralPath $source.FullName).Hash -ne (Get-FileHash -LiteralPath $destination).Hash) { throw "Changed published notice: $destination" }
        } elseif ([IO.File]::ReadAllText($source.FullName).Replace("`r`n", "`n").TrimEnd() -cne [IO.File]::ReadAllText($destination).Replace("`r`n", "`n").TrimEnd()) { throw "Stale published license: $destination" }
    }
}
Write-Output "Verified component license coverage and original notice hashes for $($packages.Count) NuGet packages."
