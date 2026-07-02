using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentUsageObserver.Providers.Codex;

/// <summary>
/// Authentication state for Codex, read from ~/.codex/auth.json.
/// </summary>
public sealed class CodexAuth
{
    public required string AuthMode { get; init; }
    public string? AccessToken { get; init; }

    public bool IsAuthenticated => !string.IsNullOrWhiteSpace(AccessToken);

    public static string AuthPath
    {
        get
        {
            var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (string.IsNullOrWhiteSpace(codexHome))
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                codexHome = Path.Combine(home, ".codex");
            }

            return Path.Combine(codexHome, "auth.json");
        }
    }

    public static CodexAuth? Load()
    {
        var path = AuthPath;
        if (!File.Exists(path))
            return null;

        try
        {
            var parsed = JsonSerializer.Deserialize<AuthFile>(File.ReadAllText(path));
            if (parsed is null)
                return null;

            return new CodexAuth
            {
                AuthMode = parsed.AuthMode ?? "unknown",
                AccessToken = parsed.Tokens?.AccessToken
            };
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed class AuthFile
    {
        [JsonPropertyName("auth_mode")]
        public string? AuthMode { get; set; }

        [JsonPropertyName("tokens")]
        public TokenBlock? Tokens { get; set; }
    }

    private sealed class TokenBlock
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }
    }
}
