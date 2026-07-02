using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using AgentUsageObserver.Models;
using AgentUsageObserver.Services.Localization;

namespace AgentUsageObserver.Services;

/// <summary>
/// Auto-actualización: al iniciar consulta la última release en GitHub, compara
/// contra la versión instalada y, si hay una más nueva, ofrece descargar y
/// ejecutar el instalador en modo semi-silencioso (/SILENT).
///
/// Es best-effort: cualquier fallo (sin red, API caída, JSON raro) se traga en
/// silencio para no molestar en el arranque.
/// </summary>
public sealed class UpdateService
{
    // owner/repo del proyecto. La API de releases es pública (sin token).
    private const string LatestReleaseUrl =
        "https://api.github.com/repos/Franco-Vallejos/AgentsUsageObserver/releases/latest";

    private readonly HttpClient _http;
    private readonly SettingsService _settings;

    public UpdateService(HttpClient http, SettingsService settings)
    {
        _http = http;
        _settings = settings;
    }

    /// <summary>
    /// Chequeo completo: consulta, compara, y si corresponde muestra el diálogo
    /// e inicia la instalación. Pensado para llamarse fire-and-forget al arrancar.
    /// </summary>
    public async Task CheckAsync()
    {
        try
        {
            if (!_settings.Current.CheckForUpdates) return;

            var info = await FetchLatestAsync().ConfigureAwait(false);
            if (info is null) return;

            Version current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            if (info.Value.Version <= current) return;

            // "Omitir esta versión": no avisar si coincide con lo que el usuario ignoró.
            if (string.Equals(_settings.Current.IgnoredUpdateVersion,
                              info.Value.Version.ToString(),
                              StringComparison.OrdinalIgnoreCase))
                return;

            // El diálogo y la descarga viven en el hilo de UI.
            await Application.Current.Dispatcher.InvokeAsync(() => Prompt(info.Value, current));
        }
        catch
        {
            // Silencioso: la actualización nunca debe tumbar el arranque.
        }
    }

    private readonly record struct ReleaseInfo(Version Version, string RawTag, string InstallerUrl);

    /// <summary>Consulta la última release y extrae versión + URL del instalador.</summary>
    private async Task<ReleaseInfo?> FetchLatestAsync()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
        // GitHub exige User-Agent en su API.
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("AgentUsageObserver", VersionString()));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var res = await _http.SendAsync(req).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode) return null;

        await using var stream = await res.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
        var root = doc.RootElement;

        if (!root.TryGetProperty("tag_name", out var tagEl)) return null;
        string tag = tagEl.GetString() ?? "";
        if (!TryParseTag(tag, out var version)) return null;

        // Busca el asset *-Setup.exe.
        string? installer = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                string name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (name.EndsWith("Setup.exe", StringComparison.OrdinalIgnoreCase) &&
                    asset.TryGetProperty("browser_download_url", out var u))
                {
                    installer = u.GetString();
                    break;
                }
            }
        }

        if (string.IsNullOrEmpty(installer)) return null;
        return new ReleaseInfo(version, tag, installer);
    }

    /// <summary>Muestra el diálogo de actualización y actúa según la respuesta.</summary>
    private void Prompt(ReleaseInfo info, Version current)
    {
        var result = MessageBox.Show(
            Loc.T(Str.UpdateAvailableMessage, info.Version.ToString(), current.ToString()),
            Loc.T(Str.UpdateAvailableTitle),
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Information);

        switch (result)
        {
            case MessageBoxResult.Yes:      // Instalar ahora
                _ = DownloadAndRunAsync(info);
                break;
            case MessageBoxResult.Cancel:   // Omitir esta versión
                var updated = _settings.Current.Clone();
                updated.IgnoredUpdateVersion = info.Version.ToString();
                _settings.Save(updated);
                break;
            // No / cerrar → "Después": no hacemos nada, vuelve a avisar el próximo arranque.
        }
    }

    /// <summary>Descarga el instalador a %TEMP% y lo ejecuta con /SILENT; luego cierra la app.</summary>
    private async Task DownloadAndRunAsync(ReleaseInfo info)
    {
        try
        {
            string dest = Path.Combine(
                Path.GetTempPath(),
                $"AgentUsageObserver-{info.Version}-Setup.exe");

            using (var res = await _http.GetAsync(info.InstallerUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
            {
                res.EnsureSuccessStatusCode();
                await using var src = await res.Content.ReadAsStreamAsync().ConfigureAwait(false);
                await using var file = File.Create(dest);
                await src.CopyToAsync(file).ConfigureAwait(false);
            }

            // /SILENT: barra de progreso del instalador, sin preguntas.
            // UseShellExecute=true → dispara el prompt de UAC (instala en Program Files).
            var psi = new ProcessStartInfo
            {
                FileName = dest,
                Arguments = "/SILENT",
                UseShellExecute = true,
            };
            Process.Start(psi);

            // Liberamos el ejecutable para que el instalador pueda sobrescribirlo.
            await Application.Current.Dispatcher.InvokeAsync(() => Application.Current.Shutdown());
        }
        catch (Exception ex)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
                MessageBox.Show(
                    Loc.T(Str.UpdateDownloadError, ex.Message),
                    Loc.T(Str.UpdateAvailableTitle),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning));
        }
    }

    /// <summary>Parsea "v0.2.0" o "0.2.0" a <see cref="Version"/>.</summary>
    private static bool TryParseTag(string tag, out Version version)
    {
        string trimmed = tag.TrimStart('v', 'V').Trim();
        return Version.TryParse(trimmed, out version!);
    }

    private static string VersionString() =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
}
