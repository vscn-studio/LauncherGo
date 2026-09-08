param([string]$AssetsFile = 'LauncherGo.App/obj/project.assets.json')
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$assets = Get-Content -LiteralPath (Join-Path $repository $AssetsFile) -Raw | ConvertFrom-Json -AsHashtable
$output = Join-Path $repository 'THIRD-PARTY-LICENSES/packages'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$packages = foreach ($entry in ($assets.libraries.GetEnumerator() | Sort-Object Key)) {
    if ($entry.Value.type -ne 'package' -or $entry.Key -like 'AvaloniaUI.DiagnosticsSupport/*') { continue }
    $packageDirectory = $null
    foreach ($root in $assets.packageFolders.Keys) {
        $candidate = Join-Path $root $entry.Value.path
        if (Test-Path -LiteralPath $candidate -PathType Container) { $packageDirectory = $candidate; break }
    }
    if (!$packageDirectory) { throw "Package missing: $($entry.Key)" }
    [xml]$nuspec = Get-Content -LiteralPath (Get-ChildItem -LiteralPath $packageDirectory -Filter '*.nuspec' | Select-Object -First 1).FullName -Raw
    $metadata = $nuspec.package.metadata
    $documents = foreach ($file in $entry.Value.files) {
        if ($file -notmatch '(^|/)(licen[sc]e|copying|notice|third.party|authors)([^/]*$)') { continue }
        $source = Join-Path $packageDirectory $file
        $hash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
        $target = Join-Path $output "$hash.txt"
        # Preserve upstream notices byte-for-byte, including copyright and line endings.
        Copy-Item -LiteralPath $source -Destination $target -Force
        [ordered]@{ source = $file; file = "packages/$hash.txt"; sha256 = $hash }
    }
    [ordered]@{
        package = $entry.Key
        copyright = [string]$metadata.copyright
        license = if ($metadata.license -is [System.Xml.XmlElement]) { $metadata.license.InnerText } else { [string]$metadata.license }
        licenseUrl = [string]$metadata.licenseUrl
        repository = [string]$metadata.repository.url
        commit = [string]$metadata.repository.commit
        project = [string]$metadata.projectUrl
        documents = @($documents)
    }
}
$index = Join-Path $repository 'THIRD-PARTY-LICENSES/nuget-packages.json'
ConvertTo-Json -InputObject @($packages) -Depth 6 | Set-Content -LiteralPath $index -Encoding utf8NoBOM
Write-Output "Updated license snapshot for $(@($packages).Count) NuGet packages. Existing notice files were not deleted."
