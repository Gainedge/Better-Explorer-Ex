$enc = [System.Text.UTF8Encoding]::new($false)

# --- MainWindow: remove duplicate lines 111-130 (0-indexed) ---
$mwPath = "E:\C#\BetterExplorerEx\BetterExplorer\MainWindow.xaml.cs"
$mwLines = [System.IO.File]::ReadAllLines($mwPath)
$mwClean = [System.Collections.Generic.List[string]]::new()
$mwClean.AddRange([string[]]$mwLines[0..109])
$mwClean.AddRange([string[]]$mwLines[110..($mwLines.Length-1)] | Where-Object { $_ -ne $null })
# Actually cut 111-130 exactly:
$mwClean2 = [System.Collections.Generic.List[string]]::new()
$mwClean2.AddRange([string[]]$mwLines[0..109])
$mwClean2.AddRange([string[]]$mwLines[131..($mwLines.Length-1)])
[System.IO.File]::WriteAllLines($mwPath, $mwClean2.ToArray(), $enc)
Write-Host "MainWindow cleaned: $($mwClean2.Count) lines"

# --- SettingsWindow: remove duplicate lines 88-111 (0-indexed) ---
$swPath = "E:\C#\BetterExplorerEx\BetterExplorer.Controls\SettingsWindow.xaml.cs"
$swLines = [System.IO.File]::ReadAllLines($swPath)
$swClean = [System.Collections.Generic.List[string]]::new()
$swClean.AddRange([string[]]$swLines[0..86])
$swClean.AddRange([string[]]$swLines[112..($swLines.Length-1)])
[System.IO.File]::WriteAllLines($swPath, $swClean.ToArray(), $enc)
Write-Host "SettingsWindow cleaned: $($swClean.Count) lines"
