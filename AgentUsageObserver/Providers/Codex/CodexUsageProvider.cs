using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentUsageObserver.Models;
using AgentUsageObserver.Services.Localization;

namespace AgentUsageObserver.Providers.Codex;

/// <summary>
/// Codex usage provider based on the latest token_count event written by Codex
/// under ~/.codex/sessions/*.jsonl.
/// </summary>
public sealed class CodexUsageProvider : Providers.IUsageProvider
{
    private readonly Func<Settings> _settings;

    public string Id => "codex";
    public string Name => "Codex";

    public CodexUsageProvider(Func<Settings> settings) => _settings = settings;

    public Task<UsageSnapshot> GetUsageAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var auth = CodexAuth.Load();
        if (auth is null || !auth.IsAuthenticated)
            return Task.FromResult(
                UsageSnapshot.NotAuthenticated(Id, Name, Loc.T(Str.ProviderSignInCodex)));

        var latest = TryReadLatestSnapshot(ct);
        if (latest is null)
            return Task.FromResult(
                UsageSnapshot.Error(Id, Name, Loc.T(Str.ProviderNoRecentUsageData)));

        var snapshot = Map(latest);
        if (!string.IsNullOrWhiteSpace(latest.RateLimitReachedType))
        {
            var wait = latest.RateLimitReachedType.Equals("secondary", StringComparison.OrdinalIgnoreCase)
                ? latest.Secondary?.ResetsAt
                : latest.Primary?.ResetsAt;
            var message = wait is { } resetAt
                ? Loc.T(Str.ProviderRateLimitRetry, FormatWait(resetAt - DateTimeOffset.UtcNow))
                : Loc.T(Str.MsgRateLimitReached);

            return Task.FromResult(
                UsageSnapshot.RateLimited(Id, Name, message, snapshot.Windows));
        }

        return Task.FromResult(snapshot);
    }

    private UsageSnapshot Map(CodexRateLimitEvent data)
    {
        var settings = _settings();
        var windows = new List<UsageWindow>();

        if (data.Primary is not null)
            windows.Add(BuildWindow(data.Primary, settings, "primary"));
        if (data.Secondary is not null)
            windows.Add(BuildWindow(data.Secondary, settings, "secondary"));

        return new UsageSnapshot(Id, Name, UsageStatus.Ok, windows, data.Timestamp);
    }

    private static UsageWindow BuildWindow(CodexWindow window, Settings settings, string fallbackKey)
    {
        var key = window.WindowMinutes switch
        {
            300 => "five_hour",
            10080 => "seven_day",
            _ => fallbackKey
        };
        var label = key switch
        {
            "five_hour" => Loc.T(Str.LabelFiveHour),
            "seven_day" => Loc.T(Str.LabelWeek),
            _ => $"{window.WindowMinutes}m"
        };

        var severity = SeverityFromThresholds(window.UsedPercent, settings);
        return new UsageWindow(key, label, window.UsedPercent, severity, window.ResetsAt);
    }

    private static UsageSeverity SeverityFromThresholds(double percent, Settings settings)
    {
        if (percent >= settings.CriticalThreshold) return UsageSeverity.Critical;
        if (percent >= settings.WarningThreshold) return UsageSeverity.Warning;
        return UsageSeverity.Normal;
    }

    private static string FormatWait(TimeSpan time)
    {
        if (time.TotalSeconds < 60) return Loc.T(Str.WaitSeconds, Math.Max(1, (int)time.TotalSeconds));
        if (time.TotalMinutes < 60) return Loc.T(Str.WaitMinutes, (int)time.TotalMinutes);
        return Loc.T(Str.WaitHours, (int)time.TotalHours);
    }

    private static CodexRateLimitEvent? TryReadLatestSnapshot(CancellationToken ct)
    {
        var sessionsDir = GetSessionsDirectory();
        if (!Directory.Exists(sessionsDir))
            return null;

        var recentFiles = new DirectoryInfo(sessionsDir)
            .EnumerateFiles("*.jsonl", SearchOption.AllDirectories)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Take(12);

        foreach (var file in recentFiles)
        {
            ct.ThrowIfCancellationRequested();
            if (TryReadLatestTokenCount(file.FullName, out var parsed))
                return parsed;
        }

        return null;
    }

    private static bool TryReadLatestTokenCount(string path, out CodexRateLimitEvent? parsed)
    {
        parsed = null;

        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (!line.Contains("\"type\":\"token_count\"", StringComparison.Ordinal))
                    continue;

                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var rootType) ||
                    rootType.GetString() != "event_msg")
                    continue;

                if (!root.TryGetProperty("payload", out var payload) ||
                    !payload.TryGetProperty("type", out var payloadType) ||
                    payloadType.GetString() != "token_count" ||
                    !payload.TryGetProperty("rate_limits", out var rateLimits))
                    continue;

                var timestamp = root.TryGetProperty("timestamp", out var timestampValue) &&
                                DateTimeOffset.TryParse(timestampValue.GetString(), out var parsedTimestamp)
                    ? parsedTimestamp.ToUniversalTime()
                    : DateTimeOffset.UtcNow;

                parsed = new CodexRateLimitEvent(
                    timestamp,
                    ParseWindow(rateLimits, "primary"),
                    ParseWindow(rateLimits, "secondary"),
                    TryGetString(rateLimits, "plan_type"),
                    TryGetString(rateLimits, "rate_limit_reached_type"));
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }

        return parsed is not null;
    }

    private static CodexWindow? ParseWindow(JsonElement rateLimits, string propertyName)
    {
        if (!rateLimits.TryGetProperty(propertyName, out var window))
            return null;

        var usedPercent = window.TryGetProperty("used_percent", out var percentValue)
            ? percentValue.GetDouble()
            : 0;
        var windowMinutes = window.TryGetProperty("window_minutes", out var minutesValue)
            ? minutesValue.GetInt32()
            : 0;
        DateTimeOffset? resetsAt = null;
        if (window.TryGetProperty("resets_at", out var resetValue) && resetValue.ValueKind == JsonValueKind.Number)
            resetsAt = DateTimeOffset.FromUnixTimeSeconds(resetValue.GetInt64());

        return new CodexWindow(usedPercent, windowMinutes, resetsAt);
    }

    private static string? TryGetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string GetSessionsDirectory()
    {
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(codexHome))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            codexHome = Path.Combine(home, ".codex");
        }

        return Path.Combine(codexHome, "sessions");
    }

    private sealed record CodexRateLimitEvent(
        DateTimeOffset Timestamp,
        CodexWindow? Primary,
        CodexWindow? Secondary,
        string? PlanType,
        string? RateLimitReachedType);

    private sealed record CodexWindow(
        double UsedPercent,
        int WindowMinutes,
        DateTimeOffset? ResetsAt);
}
