$enc = [System.Text.UTF8Encoding]::new($false)

# --- MainWindow: re-read and check if already correct (368 lines expected) ---
$mwPath = "E:\C#\BetterExplorerEx\BetterExplorer\MainWindow.xaml.cs"
$mwLines = [System.IO.File]::ReadAllLines($mwPath)
Write-Host "MainWindow lines: $($mwLines.Length)"
for ($i=105; $i -le 115; $i++) { Write-Host "$i`: $($mwLines[$i])" }

# --- SettingsWindow: duplicate region is lines 88-111 (0-indexed), remove them ---
$swPath = "E:\C#\BetterExplorerEx\BetterExplorer.Controls\SettingsWindow.xaml.cs"
$swLines = [System.IO.File]::ReadAllLines($swPath)
Write-Host "SettingsWindow lines: $($swLines.Length)"
for ($i=42; $i -le 115; $i++) { Write-Host "$i`: $($swLines[$i])" }
