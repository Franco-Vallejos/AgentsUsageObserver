using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using AgentUsageObserver.Models;
using AgentUsageObserver.Services;
using AgentUsageObserver.Services.Localization;
using AgentUsageObserver.UI;
using H.NotifyIcon;

namespace AgentUsageObserver.Tray;

/// <summary>
/// Owns the tray icon and interaction:
///  - single click toggles the mini panel
///  - double click opens settings
///  - right click opens the context menu
/// Keeps one snapshot per provider so multiple agents can coexist.
/// </summary>
public sealed class TrayController : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly SettingsService _settings;
    private readonly PollingService _polling;
    private readonly DispatcherTimer _clickTimer;
    private readonly Dictionary<string, UsageSnapshot> _snapshots = new();

    private MiniPanel? _miniPanel;
    private MainWindow? _mainWindow;
    private Icon? _currentIcon;
    private bool _doubleClickPending;

    public TrayController(SettingsService settings, PollingService polling)
    {
        _settings = settings;
        _polling = polling;

        _icon = new TaskbarIcon
        {
            Id = new Guid("8E0F7A12-BFB3-4FE8-B9A5-48FD50A15A9A"),
            ToolTipText = Loc.T(Str.AppName),
            Visibility = Visibility.Visible
        };
        _icon.TrayLeftMouseUp += OnLeftClick;
        _icon.TrayMouseDoubleClick += OnDoubleClick;
        _icon.ContextMenu = BuildContextMenu();

        _clickTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(GetDoubleClickTime())
        };
        _clickTimer.Tick += OnSingleClickConfirmed;

        UpdateIcon(null);

        try { _icon.ForceCreate(); } catch { }

        try
        {
            _icon.ShowNotification(
                title: Loc.T(Str.TrayStartupTitle),
                message: Loc.T(Str.TrayStartupMessage));
        }
        catch { }

        _polling.Updated += OnUsageUpdated;
    }

    private void OnLeftClick(object sender, RoutedEventArgs e)
    {
        _doubleClickPending = false;
        _clickTimer.Stop();
        _clickTimer.Start();
    }

    private void OnSingleClickConfirmed(object? sender, EventArgs e)
    {
        _clickTimer.Stop();
        if (_doubleClickPending)
        {
            _doubleClickPending = false;
            return;
        }

        ToggleMiniPanel();
    }

    private void OnDoubleClick(object sender, RoutedEventArgs e)
    {
        _doubleClickPending = true;
        _clickTimer.Stop();
        HideMiniPanel();
        ShowMainWindow();
    }

    private void OnUsageUpdated(UsageSnapshot snapshot)
    {
        _snapshots[snapshot.ProviderId] = snapshot;
        UpdateIcon(PrimarySnapshot());
        _miniPanel?.Update(SnapshotsOrdered(), snapshot.ProviderId);
    }

    private void UpdateIcon(UsageSnapshot? snapshot)
    {
        var newIcon = TrayIconRenderer.Render(snapshot);
        _icon.Icon = newIcon;
        _currentIcon?.Dispose();
        _currentIcon = newIcon;
        _icon.ToolTipText = BuildTooltip(_snapshots.Values);
    }

    private static string BuildTooltip(IEnumerable<UsageSnapshot> snapshots)
    {
        var ordered = snapshots
            .OrderBy(s => s.ProviderName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        if (ordered.Count == 0)
            return Loc.T(Str.TooltipLoading);

        return string.Join(Environment.NewLine, ordered.Select(FormatTooltipLine));
    }

    private static string FormatTooltipLine(UsageSnapshot snapshot)
    {
        if (snapshot.Status == UsageStatus.NotAuthenticated)
            return $"{snapshot.ProviderName} - {Loc.T(Str.StatusNoSession)}";

        if (snapshot.Status == UsageStatus.Error && snapshot.FiveHour is null)
            return $"{snapshot.ProviderName} - {Loc.T(Str.StatusNoConnection)}";

        string fiveHour = snapshot.FiveHour is { } fh
            ? Loc.T(Str.TooltipFiveHour, $"{fh.Percent:0}")
            : Loc.T(Str.TooltipFiveHourEmpty);
        string week = snapshot.SevenDay is { } wk
            ? Loc.T(Str.TooltipWeek, $"{wk.Percent:0}")
            : Loc.T(Str.TooltipWeekEmpty);
        string suffix = snapshot.Status == UsageStatus.RateLimited ? Loc.T(Str.TooltipWaiting) : "";
        return $"{snapshot.ProviderName} - {fiveHour} - {week}{suffix}";
    }

    private void ToggleMiniPanel()
    {
        if (_miniPanel is { IsVisible: true })
            HideMiniPanel();
        else
            ShowMiniPanel();
    }

    private void ShowMiniPanel()
    {
        _miniPanel ??= new MiniPanel(() => ShowMainWindow(), id => _polling.PollProvider(id));
        _miniPanel.Update(SnapshotsOrdered());
        _miniPanel.ShowNearTray();

        if (_snapshots.Values.All(snapshot => snapshot.Status != UsageStatus.RateLimited))
            _polling.PollNow();
    }

    private void HideMiniPanel() => _miniPanel?.HidePanel();

    private void ShowMainWindow()
    {
        if (_mainWindow is null)
        {
            _mainWindow = new MainWindow(_settings);
            _mainWindow.Closed += (_, _) => _mainWindow = null;
        }

        _mainWindow.Show();
        _mainWindow.Activate();
        if (_mainWindow.WindowState == WindowState.Minimized)
            _mainWindow.WindowState = WindowState.Normal;
    }

    private System.Windows.Controls.ContextMenu BuildContextMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu();

        var openItem = new System.Windows.Controls.MenuItem { Header = Loc.T(Str.MenuSettings) };
        openItem.Click += (_, _) => ShowMainWindow();

        var refreshItem = new System.Windows.Controls.MenuItem { Header = Loc.T(Str.MenuRefreshNow) };
        refreshItem.Click += (_, _) => _polling.PollNow();

        var exitItem = new System.Windows.Controls.MenuItem { Header = Loc.T(Str.MenuExit) };
        exitItem.Click += (_, _) => Application.Current.Shutdown();

        menu.Items.Add(openItem);
        menu.Items.Add(refreshItem);
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(exitItem);
        return menu;
    }

    private IReadOnlyList<UsageSnapshot> SnapshotsOrdered() =>
        _snapshots.Values
            .OrderBy(snapshot => snapshot.ProviderName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    private UsageSnapshot? PrimarySnapshot() =>
        _snapshots.Values
            .OrderByDescending(SnapshotPriority)
            .ThenBy(snapshot => snapshot.ProviderName, StringComparer.CurrentCultureIgnoreCase)
            .FirstOrDefault();

    private static int SnapshotPriority(UsageSnapshot snapshot)
    {
        var severity = snapshot.FiveHour?.Severity
            ?? snapshot.Windows.FirstOrDefault()?.Severity
            ?? UsageSeverity.Unknown;

        if (severity == UsageSeverity.Critical) return 40;
        if (severity == UsageSeverity.Warning) return 30;
        if (severity == UsageSeverity.Normal) return 20;
        if (snapshot.Status == UsageStatus.RateLimited) return 15;
        if (snapshot.Status == UsageStatus.Error) return 10;
        if (snapshot.Status == UsageStatus.NotAuthenticated) return 5;
        return 0;
    }

    private static int GetDoubleClickTime()
    {
        try { return Math.Max(200, (int)NativeMethods.GetDoubleClickTime()); }
        catch { return 300; }
    }

    public void Dispose()
    {
        _clickTimer.Stop();
        _polling.Updated -= OnUsageUpdated;
        _icon.Dispose();
        _currentIcon?.Dispose();
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern uint GetDoubleClickTime();
    }
}
