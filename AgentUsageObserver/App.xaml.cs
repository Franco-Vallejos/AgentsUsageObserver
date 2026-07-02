using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using AgentUsageObserver.Providers;
using AgentUsageObserver.Providers.Claude;
using AgentUsageObserver.Providers.Codex;
using AgentUsageObserver.Services;
using AgentUsageObserver.Services.Localization;
using AgentUsageObserver.Tray;

namespace AgentUsageObserver;

/// <summary>
/// Application composition. The app lives in the tray and starts without a
/// main window.
/// </summary>
public partial class App : Application
{
    private HttpClient? _http;
    private SettingsService? _settingsService;
    private PollingService? _polling;
    private TrayController? _tray;
    private UpdateService? _updateService;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                Loc.T(Str.UnexpectedError, args.Exception.Message),
                Loc.T(Str.AppName),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };

        _settingsService = new SettingsService();
        _settingsService.Load();

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        var refresher = new ClaudeTokenRefresher(_http);
        var providers = new List<IUsageProvider>
        {
            new ClaudeUsageProvider(_http, refresher, () => _settingsService.Current),
            new CodexUsageProvider(() => _settingsService.Current)
        };

        _polling = new PollingService(providers, () => _settingsService.Current);
        _tray = new TrayController(_settingsService, _polling);

        _settingsService.Changed += _ => _polling!.PollNow();

        _polling.Start();

        _updateService = new UpdateService(_http, _settingsService);
        _ = _updateService.CheckAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _polling?.Dispose();
        _http?.Dispose();
        base.OnExit(e);
    }
}
