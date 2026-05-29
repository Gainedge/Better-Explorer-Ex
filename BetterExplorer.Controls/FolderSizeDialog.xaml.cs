using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI;
using Windows.UI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace BetterExplorer.Controls
{
    // ── Data models ──────────────────────────────────────────────────────────

    internal sealed class FolderSizeEntry
    {
        public string Name      { get; init; } = string.Empty;
        public string Path      { get; init; } = string.Empty;
        public long   Bytes     { get; set;  }
        public Color  Color     { get; init; }
        public long   TotalBytes { get; set; }   // stamped before legend binding
        public string SizeText   => FormatBytes(Bytes);
        public string PercentText => TotalBytes > 0 ? $"{Bytes * 100.0 / TotalBytes:F1}%" : "";

        // Geometry for hit-testing (start/sweep in radians)
        public double StartAngle { get; set; }
        public double SweepAngle { get; set; }

        private static string FormatBytes(long b)
        {
            if (b >= 1L << 30) return $"{b / (double)(1L << 30):F2} GB";
            if (b >= 1L << 20) return $"{b / (double)(1L << 20):F1} MB";
            if (b >= 1L << 10) return $"{b / (double)(1L << 10):F0} KB";
            return $"{b} B";
        }
    }

    // ── Dialog ───────────────────────────────────────────────────────────────

    public sealed partial class FolderSizeDialog : ContentDialog
    {
        // Palette – enough for up to 20 slices before wrapping
        private static readonly Color[] _palette =
        [
            Color.FromArgb(255,  70, 130, 180), Color.FromArgb(255, 255, 127,  14),
            Color.FromArgb(255,  44, 160,  44), Color.FromArgb(255, 214,  39,  40),
            Color.FromArgb(255, 148, 103, 189), Color.FromArgb(255, 140,  86,  75),
            Color.FromArgb(255, 227, 119, 194), Color.FromArgb(255, 127, 127, 127),
            Color.FromArgb(255, 188, 189,  34), Color.FromArgb(255,  23, 190, 207),
            Color.FromArgb(255, 174, 199, 232), Color.FromArgb(255, 255, 187, 120),
            Color.FromArgb(255, 152, 223, 138), Color.FromArgb(255, 255, 152, 150),
            Color.FromArgb(255, 197, 176, 213), Color.FromArgb(255, 196, 156, 148),
            Color.FromArgb(255, 247, 182, 210), Color.FromArgb(255, 199, 199, 199),
            Color.FromArgb(255, 219, 219, 141), Color.FromArgb(255, 158, 218, 229),
        ];

        private readonly string _rootPath;
        private readonly List<FolderSizeEntry> _entries = [];
        private long _totalBytes;
        private long _scannedCount;

        // Redraw throttle
        private long _pendingRedraw;
        private CancellationTokenSource _cts = new();

        public FolderSizeDialog(string path)
        {
            InitializeComponent();
            _rootPath = path;
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            PathLabel.Text = _rootPath;
            ApplyRoundedCorners();
            _ = ScanAsync(_cts.Token);
        }

        // Walk the visual tree and round every Border that makes up the dialog shell.
        private void ApplyRoundedCorners()
        {
            var radius = new CornerRadius(12);
            ApplyToVisualTree(this, radius);
        }

        private static void ApplyToVisualTree(DependencyObject root, CornerRadius radius)
        {
            int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
                if (child is Border b)
                    b.CornerRadius = radius;
                ApplyToVisualTree(child, radius);
            }
        }

        // ── Scanning ─────────────────────────────────────────────────────────

        private async Task ScanAsync(CancellationToken ct)
        {
            try
            {
                // Enumerate immediate subdirs
                var enumOpts = new EnumerationOptions
                {
                    IgnoreInaccessible    = true,
                    AttributesToSkip      = FileAttributes.ReparsePoint,  // skip junctions/symlinks
                    RecurseSubdirectories = false,
                };

                string[] dirs;
                try { dirs = Directory.GetDirectories(_rootPath, "*", enumOpts); }
                catch { dirs = []; }

                long looseFiles = 0;
                try
                {
                    foreach (var f in Directory.EnumerateFiles(_rootPath, "*", enumOpts))
                    {
                        try { looseFiles += new FileInfo(f).Length; } catch { }
                    }
                }
                catch { /* ignore */ }

                // Kick off parallel dir scans
                var tasks = new List<Task>();
                int idx = 0;
                foreach (var dir in dirs)
                {
                    if (ct.IsCancellationRequested) break;
                    var entry = new FolderSizeEntry
                    {
                        Name  = System.IO.Path.GetFileName(dir),
                        Path  = dir,
                        Color = _palette[idx % _palette.Length],
                    };
                    _entries.Add(entry);
                    int capturedIdx = idx++;
                    tasks.Add(Task.Run(async () =>
                    {
                        long size = await Task.Run(() => RecurseDirectory(dir, ct), ct);
                        entry.Bytes = size;
                        Interlocked.Add(ref _totalBytes, size);
                        Interlocked.Increment(ref _scannedCount);
                        TryIncrementalRedraw((int)((double)_scannedCount / Math.Max(dirs.Length, 1) * 100));
                    }, ct));
                }

                await Task.WhenAll(tasks);
                ct.ThrowIfCancellationRequested();

                // Add loose-files slice
                if (looseFiles > 0)
                {
                    _entries.Add(new FolderSizeEntry
                    {
                        Name  = "Files in root",
                        Path  = _rootPath,
                        Bytes = looseFiles,
                        Color = Color.FromArgb(255, 160, 160, 160),
                    });
                    Interlocked.Add(ref _totalBytes, looseFiles);
                }

                // Add free space slice for drives
                try
                {
                    var di = new DriveInfo(System.IO.Path.GetPathRoot(_rootPath)!);
                    if (di.IsReady && di.AvailableFreeSpace > 0)
                    {
                        _entries.Add(new FolderSizeEntry
                        {
                            Name  = "Free space",
                            Path  = string.Empty,
                            Bytes = di.AvailableFreeSpace,
                            Color = Color.FromArgb(255, 200, 200, 200),
                        });
                        Interlocked.Add(ref _totalBytes, di.AvailableFreeSpace);
                    }
                }
                catch { /* not a drive root */ }

                // Final draw
                DispatcherQueue.TryEnqueue(() =>
                {
                    DrawPieChart();
                    RebuildLegend();
                    ScanProgress.Value   = 100;
                    StatusText.Text      = $"Total: {FolderSizeEntry_FormatBytes(_totalBytes)}";
                });
            }
            catch (OperationCanceledException) { }
        }

        private static long RecurseDirectory(string path, CancellationToken ct)
        {
            long total = 0;
            var stack = new Stack<string>();
            stack.Push(path);

            var fileOpts = new EnumerationOptions
            {
                IgnoreInaccessible    = true,
                AttributesToSkip      = FileAttributes.ReparsePoint,
                RecurseSubdirectories = false,
            };

            while (stack.Count > 0 && !ct.IsCancellationRequested)
            {
                var current = stack.Pop();

                // Sum files in this directory
                try
                {
                    foreach (var f in Directory.EnumerateFiles(current, "*", fileOpts))
                    {
                        if (ct.IsCancellationRequested) break;
                        try { total += new FileInfo(f).Length; } catch { }
                    }
                }
                catch { /* access denied — skip files in this dir */ }

                // Queue sub-directories
                try
                {
                    foreach (var sub in Directory.EnumerateDirectories(current, "*", fileOpts))
                        stack.Push(sub);
                }
                catch { /* access denied — skip sub-dirs */ }
            }

            return total;
        }

        private static string FolderSizeEntry_FormatBytes(long b)
        {
            if (b >= 1L << 30) return $"{b / (double)(1L << 30):F2} GB";
            if (b >= 1L << 20) return $"{b / (double)(1L << 20):F1} MB";
            if (b >= 1L << 10) return $"{b / (double)(1L << 10):F0} KB";
            return $"{b} B";
        }

        // ── Incremental redraw throttle ───────────────────────────────────────

        private void TryIncrementalRedraw(int progressPct)
        {
            if (Interlocked.CompareExchange(ref _pendingRedraw, 1, 0) != 0) return;
            DispatcherQueue.TryEnqueue(async () =>
            {
                DrawPieChart();
                RebuildLegend();
                ScanProgress.Value = progressPct;
                StatusText.Text    = $"Scanning… ({progressPct}%)";
                await Task.Delay(120);
                Interlocked.Exchange(ref _pendingRedraw, 0);
            });
        }

        // ── Pie-chart drawing ─────────────────────────────────────────────────

        private void DrawPieChart()
        {
            PieCanvas.Children.Clear();

            double total = _totalBytes;
            if (total <= 0) return;

            const double cx = 210, cy = 210, r = 200;
            double angle = -Math.PI / 2;   // start at top

            foreach (var entry in _entries)
            {
                if (entry.Bytes <= 0) continue;
                double sweep = entry.Bytes / total * 2 * Math.PI;

                entry.StartAngle = angle;
                entry.SweepAngle = sweep;

                var path = BuildSlice(cx, cy, r, angle, sweep, entry.Color);
                PieCanvas.Children.Add(path);

                angle += sweep;
            }
        }

        private static Microsoft.UI.Xaml.Shapes.Path BuildSlice(
            double cx, double cy, double r,
            double startAngle, double sweepAngle,
            Color fill)
        {
            double endAngle = startAngle + sweepAngle;
            double x1 = cx + r * Math.Cos(startAngle);
            double y1 = cy + r * Math.Sin(startAngle);
            double x2 = cx + r * Math.Cos(endAngle);
            double y2 = cy + r * Math.Sin(endAngle);
            bool   largeArc = sweepAngle > Math.PI;

            var fig = new PathFigure
            {
                StartPoint = new Windows.Foundation.Point(cx, cy),
                IsClosed   = true,
                IsFilled   = true,
            };
            fig.Segments.Add(new LineSegment { Point = new Windows.Foundation.Point(x1, y1) });
            fig.Segments.Add(new ArcSegment
            {
                Point          = new Windows.Foundation.Point(x2, y2),
                Size           = new Windows.Foundation.Size(r, r),
                SweepDirection = SweepDirection.Clockwise,
                IsLargeArc     = largeArc,
            });

            var geo = new PathGeometry();
            geo.Figures.Add(fig);

            return new Microsoft.UI.Xaml.Shapes.Path
            {
                Data            = geo,
                Fill            = new SolidColorBrush(fill),
                Stroke          = new SolidColorBrush(Colors.White),
                StrokeThickness = 1.5,
            };
        }

        // ── Legend ────────────────────────────────────────────────────────────

        private void RebuildLegend()
        {
            long total = _totalBytes;
            foreach (var e in _entries)
                e.TotalBytes = total;

            LegendItems.ItemsSource = null;
            LegendItems.ItemsSource = new List<FolderSizeEntry>(_entries);
        }

        // ── Slice hit-testing (pointer events) ───────────────────────────────

        private void PieCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            var pt   = e.GetCurrentPoint(PieCanvas).Position;
            const double cx = 210, cy = 210, r = 200;

            double dx    = pt.X - cx;
            double dy    = pt.Y - cy;
            double dist  = Math.Sqrt(dx * dx + dy * dy);

            if (dist > r || _totalBytes <= 0)
            {
                HideTooltip();
                return;
            }

            double angle = Math.Atan2(dy, dx);
            // Normalise to the same domain used when drawing (starting at -PI/2)
            double baseAngle = -Math.PI / 2;
            double a = NormaliseAngle(angle - baseAngle);

            FolderSizeEntry? hit = null;
            foreach (var entry in _entries)
            {
                if (entry.SweepAngle <= 0) continue;
                double s = NormaliseAngle(entry.StartAngle - baseAngle);
                double e2 = s + entry.SweepAngle;
                if (a >= s && a < e2) { hit = entry; break; }
            }

            if (hit is null) { HideTooltip(); return; }

            double pct = _totalBytes > 0 ? hit.Bytes * 100.0 / _totalBytes : 0;
            TooltipName.Text    = hit.Name;
            TooltipSize.Text    = hit.SizeText;
            TooltipPercent.Text = $"{pct:F1} %";

            SlicePopup.HorizontalOffset = pt.X + 16;
            SlicePopup.VerticalOffset   = pt.Y - 10;
            SlicePopup.IsOpen           = true;
        }

        private void PieCanvas_PointerExited(object sender, PointerRoutedEventArgs e)
            => HideTooltip();

        private void HideTooltip() => SlicePopup.IsOpen = false;

        private static double NormaliseAngle(double a)
        {
            const double pi2 = 2 * Math.PI;
            while (a < 0)      a += pi2;
            while (a >= pi2)   a -= pi2;
            return a;
        }
    }
}
