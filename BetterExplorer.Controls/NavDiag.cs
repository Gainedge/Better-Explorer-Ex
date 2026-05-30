using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace BetterExplorer.Controls;

/// <summary>
/// Lightweight per-navigation phase timer.
/// Usage:
///   var d = new NavDiag("C:\\Foo");
///   d.Mark("Cleared");
///   ...
///   d.Mark("Enumerated");
///   d.Finish("Showed items");    // prints full report to Debug output
/// </summary>
internal sealed class NavDiag {
  private readonly string           _path;
  private readonly long             _t0 = Stopwatch.GetTimestamp();
  private long                      _prev;
  private readonly List<(string Phase, double Ms)> _phases = new();

  public NavDiag(string path) {
    _path = path;
    _prev = _t0;
  }

  public void Mark(string phase) {
    var now = Stopwatch.GetTimestamp();
    double ms = (now - _prev) * 1000.0 / Stopwatch.Frequency;
    _prev = now;
    _phases.Add((phase, ms));
  }

  public void Finish(string lastPhase) {
    Mark(lastPhase);
    double total = (Stopwatch.GetTimestamp() - _t0) * 1000.0 / Stopwatch.Frequency;

    var sb = new StringBuilder();
    sb.AppendLine($"[NavDiag] {_path} — total {total:F1} ms");
    foreach (var (phase, ms) in _phases)
      sb.AppendLine($"  {ms,7:F1} ms  {phase}");
    Debug.WriteLine(sb.ToString());
  }
}
