$root = "E:\C#\BetterExplorerEx"
$patch = [System.IO.File]::ReadAllLines("$root\_patch_hide.cs")

# MainWindow: replace old lines 65-90 (0-indexed) with new block
$mwPath = "$root\BetterExplorer\MainWindow.xaml.cs"
$mwLines = [System.IO.File]::ReadAllLines($mwPath)
$mwNew = [System.Collections.Generic.List[string]]::new()
$mwNew.AddRange([string[]]$mwLines[0..64])
$mwNew.AddRange([string[]]$patch)
$mwNew.AddRange([string[]]$mwLines[91..($mwLines.Length-1)])
[System.IO.File]::WriteAllLines($mwPath, $mwNew.ToArray(), [System.Text.UTF8Encoding]::new($false))
Write-Host "MainWindow done: $($mwNew.Count) lines"

# SettingsWindow: replace old lines 42-63 (0-indexed) with new block
$swPath = "$root\BetterExplorer.Controls\SettingsWindow.xaml.cs"
$swLines = [System.IO.File]::ReadAllLines($swPath)
$swNew = [System.Collections.Generic.List[string]]::new()
$swNew.AddRange([string[]]$swLines[0..41])
$swNew.AddRange([string[]]$patch)
$swNew.AddRange([string[]]$swLines[64..($swLines.Length-1)])
[System.IO.File]::WriteAllLines($swPath, $swNew.ToArray(), [System.Text.UTF8Encoding]::new($false))
Write-Host "SettingsWindow done: $($swNew.Count) lines"
