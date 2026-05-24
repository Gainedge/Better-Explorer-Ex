using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using BetterExplorer.ShellApi.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace BetterExplorer.Controls;

/// <summary>
/// Presents a Windows 11-style shell context menu using <see cref="CommandBarFlyout"/>.
///
/// Primary commands  — the horizontal icon-button strip (Open / Cut / Copy / Paste /
///                     Rename / Delete), exactly as in the Win11 shell menu.
/// Secondary commands — all items from IContextMenu (with cascading sub-menus) plus
///                      a separator before Properties at the very bottom.
/// </summary>
internal static class ShellContextMenuFlyout {

  // ── Verb → Segoe MDL2 glyph ──────────────────────────────────────────────

  private static readonly Dictionary<string, string> _verbGlyphs =
      new(StringComparer.OrdinalIgnoreCase) {
    ["open"]           = "\uE8A7",
    ["openas"]         = "\uE7AC",
    ["openwith"]       = "\uE7AC",
    ["explore"]        = "\uEC50",
    ["find"]           = "\uE721",
    ["cut"]            = "\uE8C6",
    ["copy"]           = "\uE8C8",
    ["paste"]          = "\uE77F",
    ["link"]           = "\uE71B",
    ["delete"]         = "\uE74D",
    ["rename"]         = "\uE8AC",
    ["properties"]     = "\uE946",
    ["share"]          = "\uE72D",
    ["print"]          = "\uE749",
    ["compress"]       = "\uE8B6",
    ["extract"]        = "\uEB42",
    ["pin"]            = "\uE840",
    ["unpin"]          = "\uE77A",
    ["sendto"]         = "\uE89A",
    ["runas"]          = "\uE7EF",
    ["new"]            = "\uE710",
    ["opencontaining"] = "\uE838",
  };

  // Verbs that are presented as primary (toolbar) buttons; skip them from the
  // secondary (list) section to avoid duplicates.
  private static readonly HashSet<string> _primaryVerbs =
      new(StringComparer.OrdinalIgnoreCase) {
    "open", "openas", "openwith", "cut", "copy", "paste", "delete", "rename", "properties",
  };

  private static readonly HashSet<string> _skipLabels =
      new(StringComparer.OrdinalIgnoreCase) {
    "open", "open with", "cut", "copy", "paste", "delete", "rename", "properties",
  };

  // ── Acrylic flyout presenter style ───────────────────────────────────────

  private static Style BuildPresenterStyle(XamlRoot root) {
    bool light = root.Content is FrameworkElement fe &&
                 fe.ActualTheme == ElementTheme.Light;

    var acrylic = new AcrylicBrush {
      TintColor     = light
          ? Windows.UI.Color.FromArgb(255, 243, 243, 243)
          : Windows.UI.Color.FromArgb(255, 32, 32, 32),
      FallbackColor = light
          ? Windows.UI.Color.FromArgb(255, 233, 233, 233)
          : Windows.UI.Color.FromArgb(255, 44, 44, 44),
      TintOpacity           = light ? 0.75 : 0.80,
      TintLuminosityOpacity = 0.0,
    };

    var style = new Style(typeof(CommandBarFlyoutCommandBar));
    style.Setters.Add(new Setter(Control.BackgroundProperty, acrylic));
    style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
    style.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(8)));
    return style;
  }

  // ── Entry point ──────────────────────────────────────────────────────────

  public static async Task ShowAsync(
      IReadOnlyList<string> paths,
      string                currentFolder,
      IntPtr                hwnd,
      Windows.Foundation.Point point,
      FrameworkElement      anchor,
      ShellListView         shellListView) {
    if (paths.Count == 0)
      return;

    ShellContextMenuSession? session = null;
    try {
      session = await ShellContextMenuService.QueryAsync(paths, hwnd);
    } catch (Exception ex) {
      System.Diagnostics.Debug.WriteLine($"[ShellContextMenu] QueryAsync failed: {ex}");
    }

    bool isMulti  = paths.Count > 1;
    bool isFolder = paths.Count == 1 && System.IO.Directory.Exists(paths[0]);
    bool hasClip  = shellListView.HasClipboardContent;

    var flyout = new CommandBarFlyout { AlwaysExpanded = true };

    // ── Primary commands — horizontal icon strip ──────────────────────────

    if (!isMulti) {
      flyout.PrimaryCommands.Add(MakePrimaryButton(
          isFolder ? "\uEC50" : "\uE8A7",
          isFolder ? "Open folder" : "Open",
          _ => { flyout.Hide(); shellListView.OpenSelected(); }));

      if (!isFolder) {
        flyout.PrimaryCommands.Add(MakePrimaryButton(
            "\uE7AC", "Open with",
            _ => { flyout.Hide(); _ = session?.InvokeVerbAsync("openwith", hwnd); }));
      }
    }

    flyout.PrimaryCommands.Add(MakePrimaryButton(
        "\uE8C6", "Cut  Ctrl+X",
        async _ => { flyout.Hide(); await shellListView.CutSelectedToClipboardAsync(); }));

    flyout.PrimaryCommands.Add(MakePrimaryButton(
        "\uE8C8", "Copy  Ctrl+C",
        async _ => { flyout.Hide(); await shellListView.CopySelectedToClipboardAsync(); }));

    flyout.PrimaryCommands.Add(MakePrimaryButton(
        "\uE77F", "Paste  Ctrl+V",
        async _ => { flyout.Hide(); await shellListView.PasteFromClipboardAsync(); },
        enabled: hasClip));

    flyout.PrimaryCommands.Add(MakePrimaryButton(
        "\uE8AC", "Rename  F2",
        _ => { flyout.Hide(); shellListView.BeginRename(); },
        enabled: !isMulti));

    flyout.PrimaryCommands.Add(MakePrimaryButton(
        "\uE74D", "Delete  Del",
        async _ => { flyout.Hide(); await shellListView.DeleteSelectedAsync(); }));

    flyout.PrimaryCommands.Add(MakePrimaryButton(
        "\uE946", "Properties  Alt+Enter",
        _ => { flyout.Hide(); shellListView.ShowPropertiesForSelected(); }));

    // ── Secondary commands — shell extension items ────────────────────────

    if (session is not null) {
      bool pendingSep = false;
      bool addedAny   = false;

      foreach (var item in session.Items) {
        if (item.IsSeparator) {
          pendingSep = true;
          continue;
        }

        if (string.IsNullOrWhiteSpace(item.Label))
          continue;

        if (!string.IsNullOrEmpty(item.Verb) && _primaryVerbs.Contains(item.Verb))
          continue;
        if (_skipLabels.Contains(item.Label))
          continue;

        // Only emit a separator when there is already a real item above it.
        if (pendingSep && addedAny)
          flyout.SecondaryCommands.Add(new AppBarSeparator());
        pendingSep = false;

        ICommandBarElement el = item.HasSubmenu
            ? BuildSubItem(item, flyout, hwnd, currentFolder, session, shellListView)
            : BuildSecondaryButton(item, flyout, hwnd, currentFolder, session);

        flyout.SecondaryCommands.Add(el);
        addedAny = true;
      }
    }

    // Dispose the session after the flyout closes AND any pending invocation finishes.
    flyout.Closed += (_, _) => session?.Dispose();

    // ── Show ─────────────────────────────────────────────────────────────

    flyout.ShowAt(anchor, new FlyoutShowOptions {
      Position  = point,
      ShowMode  = FlyoutShowMode.Standard,
      Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft,
    });
  }

  // ── Primary command builder ───────────────────────────────────────────────

  private static AppBarButton MakePrimaryButton(
      string glyph,
      string tooltip,
      Action<object> clickAction,
      bool enabled = true) {
    var btn = new AppBarButton {
      Icon      = new FontIcon { Glyph = glyph, FontSize = 16 },
      IsEnabled = enabled,
      Width     = 40,
    };
    ToolTipService.SetToolTip(btn, tooltip);
    btn.Click += (s, _) => clickAction(s);
    return btn;
  }

  // ── Secondary (list) item builders ───────────────────────────────────────

  // ── Icon helper ───────────────────────────────────────────────────────────

  /// <summary>
  /// Returns the best available <see cref="IconElement"/> for a shell menu item:
  /// 1. HBITMAP pixels captured on the STA thread → <see cref="WriteableBitmap"/>.
  /// 2. Known-verb Segoe MDL2 glyph → <see cref="FontIcon"/>.
  /// 3. null (no icon).
  /// Must be called on the UI thread.
  /// </summary>
  private static IconElement? IconFromItem(ShellContextMenuItem item) {
    // 1 — real bitmap from the HMENU
    if (item.IconPixels is { Length: > 0 } px && item.IconW > 0 && item.IconH > 0) {
      try {
        var wb = NativeShell.PixelsToBitmapSync(px, item.IconW, item.IconH);
        if (wb is not null)
          return new ImageIcon { Source = wb, Width = 16, Height = 16 };
      } catch { }
    }

    // 2 — Segoe MDL2 glyph for known verbs
    if (!string.IsNullOrEmpty(item.Verb) && _verbGlyphs.TryGetValue(item.Verb, out var glyph))
      return new FontIcon { Glyph = glyph };

    return null;
  }

  private static AppBarButton BuildSecondaryButton(
      ShellContextMenuItem item,
      CommandBarFlyout flyout,
      IntPtr hwnd,
      string? workingDir,
      ShellContextMenuSession? session) {
    var btn = new AppBarButton {
      Label     = item.Label,
      IsEnabled = !item.IsDisabled,
    };

    btn.Icon = IconFromItem(item);

    var capturedId  = item.Id;
    var capturedSes = session;
    btn.Click += (_, _) => {
      flyout.Hide();
      _ = Task.Run(async () => {
        try { await (capturedSes?.InvokeCommandAsync(capturedId, hwnd, workingDir) ?? Task.CompletedTask); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ShellContextMenu] InvokeCommand failed: {ex}"); }
      });
    };
    return btn;
  }

  /// <summary>
  /// Builds a cascading submenu entry.  WinUI 3's <see cref="CommandBarFlyout"/>
  /// secondary commands only support flat <see cref="AppBarButton"/>s directly, so
  /// we embed a <see cref="MenuFlyoutSubItem"/>-style tree inside an
  /// <see cref="AppBarButton"/> whose <see cref="AppBarButton.Flyout"/> is a
  /// <see cref="MenuFlyout"/> containing the children.
  /// </summary>
  private static AppBarButton BuildSubItem(
      ShellContextMenuItem item,
      CommandBarFlyout flyout,
      IntPtr hwnd,
      string? workingDir,
      ShellContextMenuSession? session,
      ShellListView? shellListView) {
    var btn = new AppBarButton {
      Label     = item.Label,
      IsEnabled = !item.IsDisabled,
    };

    btn.Icon = IconFromItem(item);

    var subFlyout = new MenuFlyout();
    bool isNewMenu = string.Equals(item.Label, "New", StringComparison.OrdinalIgnoreCase)
                  || string.Equals(item.Verb,  "NewFolder", StringComparison.OrdinalIgnoreCase);
    if (item.SubItems is not null)
      PopulateMenuFlyout(subFlyout.Items, item.SubItems, flyout, hwnd, workingDir, session,
          shellListView, isNewMenu);
    btn.Flyout = subFlyout;

    return btn;
  }

  private static void PopulateMenuFlyout(
      IList<MenuFlyoutItemBase> target,
      IReadOnlyList<ShellContextMenuItem> items,
      CommandBarFlyout flyout,
      IntPtr hwnd,
      string? workingDir,
      ShellContextMenuSession? session,
      ShellListView? shellListView = null,
      bool isNewMenu = false) {
    foreach (var child in items) {
      if (child.IsSeparator) {
        target.Add(new MenuFlyoutSeparator());
        continue;
      }

      if (string.IsNullOrWhiteSpace(child.Label))
        continue;

      if (child.HasSubmenu && child.SubItems is { Count: > 0 }) {
        var sub = new MenuFlyoutSubItem {
          Text      = child.Label,
          IsEnabled = !child.IsDisabled,
        };
        sub.Icon = IconFromItem(child);

        PopulateMenuFlyout(sub.Items, child.SubItems, flyout, hwnd, workingDir, session,
            shellListView, isNewMenu);
        target.Add(sub);
      } else {
        var mfi = new MenuFlyoutItem {
          Text      = child.Label,
          IsEnabled = !child.IsDisabled,
        };
        mfi.Icon = IconFromItem(child);

        var capturedId  = child.Id;
        var capturedSes = session;
        var capturedSlv = shellListView;
        var capturedNew = isNewMenu;
        mfi.Click += (_, _) => {
          flyout.Hide();
          if (capturedNew && capturedSlv is not null)
            capturedSlv.BeginRenameOnNewItem(workingDir ?? string.Empty);
          _ = Task.Run(async () => {
            try { await (capturedSes?.InvokeCommandAsync(capturedId, hwnd, workingDir) ?? Task.CompletedTask); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ShellContextMenu] InvokeCommand failed: {ex}"); }
          });
        };
        target.Add(mfi);
      }
    }
  }

  // ── Background (empty-space) context menu ────────────────────────────────

  // Shell background menu labels we replace with our own submenus/buttons.
  private static readonly HashSet<string> _bgSkipLabels =
      new(StringComparer.OrdinalIgnoreCase) {
    "View", "Sort by", "Group by", "Refresh",
    // Some localisations differ; cover the verbs too.
  };

  private static readonly HashSet<string> _bgSkipVerbs =
      new(StringComparer.OrdinalIgnoreCase) {
    "view", "sortbyprop", "groupbyprop", "refresh",
  };

  /// <summary>
  /// Shows a Windows 11-style background (folder empty-space) context menu.
  /// View / Sort By / Group By / Refresh are remapped to ShellListView methods;
  /// all other shell extension items pass through unchanged.
  /// </summary>
  public static async Task ShowBackgroundAsync(
      string           folderPath,
      IntPtr           hwnd,
      Windows.Foundation.Point point,
      FrameworkElement anchor,
      ShellListView    shellListView) {

    ShellContextMenuSession? session = null;
    try {
      // Use IShellFolder::CreateViewObject — the true SVGIO_BACKGROUND path.
      session = await ShellContextMenuService.QueryBackgroundAsync(folderPath, hwnd);
    } catch (Exception ex) {
      System.Diagnostics.Debug.WriteLine($"[ShellContextMenu/BG] QueryBackgroundAsync failed: {ex}");
    }

    bool hasClip = shellListView.HasClipboardContent;

    var flyout = new CommandBarFlyout { AlwaysExpanded = true };

    // ── Primary strip ─────────────────────────────────────────────────────

    flyout.PrimaryCommands.Add(MakePrimaryButton(
        "\uE77F", "Paste  Ctrl+V",
        async _ => await shellListView.PasteFromClipboardAsync(),
        enabled: hasClip));

    flyout.PrimaryCommands.Add(MakePrimaryButton(
        "\uE72C", "Refresh  F5",
        _ => shellListView.Refresh()));

    // ── Our remapped View / Sort By / Group By submenus ───────────────────

    // View
    var viewBtn = new AppBarButton {
      Label = "View",
      Icon  = new FontIcon { Glyph = "\uE8B9" },
    };
    var viewFlyout = new MenuFlyout();
    void AddViewItem(string label, BetterExplorer.ShellApi.ShellViewMode mode) {
      var mfi = new MenuFlyoutItem { Text = label };
      if (shellListView.ViewMode == mode)
        mfi.Icon = new FontIcon { Glyph = "\uE73E" }; // checkmark
      mfi.Click += (_, _) => shellListView.ViewMode = mode;
      viewFlyout.Items.Add(mfi);
    }
    AddViewItem("Extra Large Icons", BetterExplorer.ShellApi.ShellViewMode.ExtraLargeIcons);
    AddViewItem("Large Icons",       BetterExplorer.ShellApi.ShellViewMode.LargeIcons);
    AddViewItem("Medium Icons",      BetterExplorer.ShellApi.ShellViewMode.MediumIcons);
    AddViewItem("Small Icons",       BetterExplorer.ShellApi.ShellViewMode.SmallIcons);
    viewFlyout.Items.Add(new MenuFlyoutSeparator());
    AddViewItem("List",    BetterExplorer.ShellApi.ShellViewMode.List);
    AddViewItem("Details", BetterExplorer.ShellApi.ShellViewMode.Details);
    AddViewItem("Tiles",   BetterExplorer.ShellApi.ShellViewMode.Tiles);
    AddViewItem("Content", BetterExplorer.ShellApi.ShellViewMode.Content);
    viewBtn.Flyout = viewFlyout;
    flyout.SecondaryCommands.Add(viewBtn);

    // Sort By
    var sortBtn = new AppBarButton {
      Label = "Sort by",
      Icon  = new FontIcon { Glyph = "\uE8CB" },
    };
    var sortFlyout = new MenuFlyout();
    void AddSortItem(string label, string column) {
      var mfi = new MenuFlyoutItem { Text = label };
      if (string.Equals(shellListView.SortColumn, column, StringComparison.OrdinalIgnoreCase))
        mfi.Icon = new FontIcon { Glyph = shellListView.SortAscending ? "\uE74A" : "\uE74B" };
      mfi.Click += (_, _) => {
        bool asc = string.Equals(shellListView.SortColumn, column, StringComparison.OrdinalIgnoreCase)
            ? !shellListView.SortAscending
            : true;
        shellListView.ApplySort(column, asc);
      };
      sortFlyout.Items.Add(mfi);
    }
    AddSortItem("Name",              "Name");
    AddSortItem("Date modified",     "Date");
    AddSortItem("Type",              "Type");
    AddSortItem("Size",              "Size");
    sortFlyout.Items.Add(new MenuFlyoutSeparator());
    var sortAscItem  = new MenuFlyoutItem { Text = "Ascending" };
    var sortDescItem = new MenuFlyoutItem { Text = "Descending" };
    if ( shellListView.SortAscending) sortAscItem.Icon  = new FontIcon { Glyph = "\uE73E" };
    if (!shellListView.SortAscending) sortDescItem.Icon = new FontIcon { Glyph = "\uE73E" };
    sortAscItem.Click  += (_, _) => shellListView.ApplySort(shellListView.SortColumn, true);
    sortDescItem.Click += (_, _) => shellListView.ApplySort(shellListView.SortColumn, false);
    sortFlyout.Items.Add(sortAscItem);
    sortFlyout.Items.Add(sortDescItem);
    sortBtn.Flyout = sortFlyout;
    flyout.SecondaryCommands.Add(sortBtn);

    // Group By
    var groupBtn = new AppBarButton {
      Label = "Group by",
      Icon  = new FontIcon { Glyph = "\uF168" },
    };
    var groupFlyout = new MenuFlyout();
    void AddGroupItem(string label, string? column) {
      var mfi = new MenuFlyoutItem { Text = label };
      bool active = string.Equals(shellListView.GroupColumn,
          column ?? string.Empty, StringComparison.OrdinalIgnoreCase);
      if (active) mfi.Icon = new FontIcon { Glyph = "\uE73E" };
      mfi.Click += (_, _) => shellListView.ApplyGroupBy(column);
      groupFlyout.Items.Add(mfi);
    }
    AddGroupItem("Name",          "Name");
    AddGroupItem("Date modified", "Date");
    AddGroupItem("Type",          "Type");
    AddGroupItem("Size",          "Size");
    groupFlyout.Items.Add(new MenuFlyoutSeparator());
    AddGroupItem("(None)", null);
    groupBtn.Flyout = groupFlyout;
    flyout.SecondaryCommands.Add(groupBtn);

    flyout.SecondaryCommands.Add(new AppBarSeparator());

    // ── Shell extension items (skip ones we already cover above) ──────────

    if (session is not null) {
      bool pendingSep = false;
      bool addedAny   = false;

      foreach (var item in session.Items) {
        if (item.IsSeparator) {
          pendingSep = true;
          continue;
        }

        if (string.IsNullOrWhiteSpace(item.Label))
          continue;

        // Skip items we've remapped to our own controls.
        if (_bgSkipLabels.Contains(item.Label))
          continue;
        if (!string.IsNullOrEmpty(item.Verb) && _bgSkipVerbs.Contains(item.Verb))
          continue;
        // Also skip the standard paste/undo since we have paste in the primary strip
        // and undo can go through the shell verb naturally; only skip paste label.
        if (_skipLabels.Contains(item.Label))
          continue;

        if (pendingSep && addedAny)
          flyout.SecondaryCommands.Add(new AppBarSeparator());
        pendingSep = false;

        ICommandBarElement el = item.HasSubmenu
            ? BuildSubItem(item, flyout, hwnd, folderPath, session, shellListView)
            : BuildSecondaryButton(item, flyout, hwnd, folderPath, session);

        flyout.SecondaryCommands.Add(el);
        addedAny = true;
      }
    }

    // ── Cleanup on close ──────────────────────────────────────────────────

    flyout.Closed += (_, _) => session?.Dispose();

    // ── Show ─────────────────────────────────────────────────────────────

    flyout.ShowAt(anchor, new FlyoutShowOptions {
      Position  = point,
      ShowMode  = FlyoutShowMode.Standard,
      Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft,
    });
  }
}
