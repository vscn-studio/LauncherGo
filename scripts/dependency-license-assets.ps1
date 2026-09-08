# Only assets that can be copied into the application belong to the release
# inventory. SDK tools such as ILLink appear in libraries but have build assets.
function Get-ReleaseLicensePackages([System.Collections.IDictionary]$Assets) {
    if (!$Assets.Contains('targets') -or !$Assets.targets.Count -or !$Assets.Contains('libraries')) {
        throw 'Invalid project.assets.json: targets and libraries are required.'
    }
    $required = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($target in $Assets.targets.Values) {
        foreach ($entry in $target.GetEnumerator()) {
            if ($Assets.libraries[$entry.Key].type -ne 'package') { continue }
            foreach ($kind in @('runtime','native','runtimeTargets','resource','resources','contentFiles')) {
                $files = $entry.Value[$kind]
                if (!$files) { continue }
                foreach ($file in $files.Keys) {
                    if ($file -notmatch '(^|/)_\._$') { [void]$required.Add($entry.Key); break }
                }
            }
        }
    }
    # ReleaseAssets removes this debug-only component in the application project.
    $required | Where-Object { $_ -notlike 'AvaloniaUI.DiagnosticsSupport/*' } | Sort-Object
}
