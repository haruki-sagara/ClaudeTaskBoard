using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ClaudeTaskBoard;

// The plan's weekly limit: what `claude` /usage shows. Error is non-empty when it could not be read.
public sealed record UsageResult(int? PercentUsed, DateTimeOffset? ResetsAt, string Error, TimeSpan RetryIn);

// Reads the weekly usage from the endpoint Claude Code itself uses (undocumented, so it may change).
// The login lives in Claude Code's own credentials file. The desktop app does not keep that file fresh, so
// an expiring access token is refreshed here the way the CLI does it, and the result is written back there.
// Tokens go to the two hosts below only, are never logged, and are not kept beyond the request.
public static class UsageClient
{
    const string Url = "https://api.anthropic.com/api/oauth/usage";
    const string TokenUrl = "https://platform.claude.com/v1/oauth/token";
    const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e"; // Claude Code's public OAuth client
    const string Beta = "oauth-2025-04-20";
    public const string ErrNoLogin = "CLI のログイン情報がありません";
    public const string ErrExpired = "CLI の認証が期限切れです";
    public const string ErrRelogin = "CLI の再ログインが必要です（claude /login）";
    const string ErrThrottled = "取得回数の制限中です";
    const string ErrFailed = "取得できませんでした";

    static readonly TimeSpan Normal = TimeSpan.FromMinutes(5);
    static readonly TimeSpan Throttled = TimeSpan.FromMinutes(15);
    // No network: only re-reads the local file. Short, so that a finished login shows up right away.
    static readonly TimeSpan RecheckToken = TimeSpan.FromSeconds(5);
    static readonly TimeSpan ExpiryMargin = TimeSpan.FromMinutes(5);  // same margin as the CLI

    static readonly string? HomeOverride = Environment.GetEnvironmentVariable("TASKBOARD_HOME") is { Length: > 0 } h ? h : null;
    // TASKBOARD_HOME isolates test runs from the real login too.
    static readonly string CredentialsPath = HomeOverride is not null
        ? Path.Combine(HomeOverride, ".credentials.json")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");

    // Refresh tokens rotate, so only one process may spend them: the running board, set by App.
    public static bool AllowRefresh { get; set; }
    // A refresh token the server turned down (as a hash): it is not retried until the file holds another one.
    static string? _rejected;

    // No redirects: a token must not follow a response to some other host.
    static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    static string UserAgent => $"ClaudeTaskBoard/{typeof(UsageClient).Assembly.GetName().Version}";

    public static async Task<UsageResult> FetchAsync()
    {
        string json;
        try { json = await File.ReadAllTextAsync(CredentialsPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(null, null, ErrNoLogin, RecheckToken);
        }

        var (token, problem) = ReadToken(json, DateTimeOffset.UtcNow);
        if (token is null && problem == ErrExpired && AllowRefresh && HomeOverride is null)
        {
            var (fresh, failure) = await RefreshAsync(json);
            if (failure is not null) return failure;
            token = fresh;
        }
        if (token is null) return new(null, null, problem, RecheckToken);

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, Url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.TryAddWithoutValidation("anthropic-beta", Beta);
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            using var res = await Http.SendAsync(req);

            if (res.StatusCode == HttpStatusCode.TooManyRequests)
                return new(null, null, ErrThrottled, res.Headers.RetryAfter?.Delta is { } d && d > Throttled ? d : Throttled);
            // The server's answer, so the next try is a request again: not at the local file's pace.
            if (res.StatusCode == HttpStatusCode.Unauthorized) return new(null, null, ErrExpired, Normal);
            if (!res.IsSuccessStatusCode)
            {
                Log.Info($"usage: http {(int)res.StatusCode}");
                return new(null, null, ErrFailed, Normal);
            }
            var parsed = Parse(await res.Content.ReadAsStringAsync());
            if (parsed is null) Log.Info("usage: unexpected response shape");
            return parsed ?? new(null, null, ErrFailed, Normal);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Log.Info($"usage: {ex.GetType().Name}"); // type only: nothing from the request reaches the log
            return new(null, null, ErrFailed, Normal);
        }
    }

    // Spends the refresh token and saves what comes back. Either a usable access token or a result to show.
    static async Task<(string? token, UsageResult? failure)> RefreshAsync(string json)
    {
        if (ReadRefreshToken(json) is not { } login) return (null, new(null, null, ErrRelogin, RecheckToken));
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(login.refresh)));
        if (hash == _rejected) return (null, new(null, null, ErrRelogin, RecheckToken));

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
            {
                Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token", ["refresh_token"] = login.refresh,
                    ["client_id"] = ClientId, ["scope"] = login.scope,
                }), Encoding.UTF8, "application/json"),
            };
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            using var res = await Http.SendAsync(req);

            if (res.StatusCode == HttpStatusCode.TooManyRequests) return (null, new(null, null, ErrThrottled, Throttled));
            if (res.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                Log.Info($"token refresh: http {(int)res.StatusCode} {ErrorSummary(await res.Content.ReadAsStringAsync())}");
                // The CLI may have just refreshed it itself: then the file already holds a good one.
                var (other, _) = ReadToken(await File.ReadAllTextAsync(CredentialsPath), DateTimeOffset.UtcNow);
                if (other is not null) return (other, null);
                _rejected = hash;
                return (null, new(null, null, ErrRelogin, RecheckToken));
            }
            if (!res.IsSuccessStatusCode)
            {
                Log.Info($"token refresh: http {(int)res.StatusCode}");
                return (null, new(null, null, ErrFailed, Normal));
            }

            string body = await res.Content.ReadAsStringAsync();
            // On the freshest file, so that anything else written to it meanwhile is kept.
            string latest = await File.ReadAllTextAsync(CredentialsPath);
            if (ApplyRefresh(latest, body, DateTimeOffset.UtcNow) is not { } updated)
            {
                Log.Info("token refresh: unexpected response shape");
                return (null, new(null, null, ErrFailed, Normal));
            }
            SaveCredentials(updated);
            Log.Info("token refresh: ok");
            return (ReadToken(updated, DateTimeOffset.UtcNow).token, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            Log.Info($"token refresh: {ex.GetType().Name}");
            return (null, new(null, null, ErrFailed, Normal));
        }
    }

    // The server's reason, for the log. Long unbroken strings are cut in case one is token-like.
    public static string ErrorSummary(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("error", out var e)) return "";
            static string Str(JsonElement o, string name) =>
                o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            string code = e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : Str(e, "type");
            string text = Str(doc.RootElement, "error_description") is { Length: > 0 } d ? d : Str(e, "message");
            string all = Regex.Replace($"{code} {text}".Trim(), @"\S{40,}", "…");
            return all.Length > 160 ? all[..160] : all;
        }
        catch (JsonException) { return ""; }
    }

    // Whole file or nothing: a half-written file would log the CLI out.
    static void SaveCredentials(string json)
    {
        string tmp = CredentialsPath + "." + Environment.ProcessId + ".tmp";
        try
        {
            File.WriteAllText(tmp, json);
            File.Move(tmp, CredentialsPath, overwrite: true);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    // The token, or why it cannot be used. One about to expire counts as expired.
    public static (string? token, string problem) ReadToken(string credentialsJson, DateTimeOffset now)
    {
        try
        {
            using var doc = JsonDocument.Parse(credentialsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("claudeAiOauth", out var o) || o.ValueKind != JsonValueKind.Object ||
                !o.TryGetProperty("accessToken", out var t) || t.ValueKind != JsonValueKind.String ||
                t.GetString() is not { Length: > 0 } token) return (null, ErrNoLogin);
            if (o.TryGetProperty("expiresAt", out var e) && e.ValueKind == JsonValueKind.Number &&
                e.TryGetInt64(out long ms) && ms <= (now + ExpiryMargin).ToUnixTimeMilliseconds())
                return (null, ErrExpired);
            return (token, "");
        }
        catch (JsonException) { return (null, ErrNoLogin); }
    }

    public static (string refresh, string scope)? ReadRefreshToken(string credentialsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(credentialsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("claudeAiOauth", out var o) || o.ValueKind != JsonValueKind.Object ||
                !o.TryGetProperty("refreshToken", out var r) || r.ValueKind != JsonValueKind.String ||
                r.GetString() is not { Length: > 0 } refresh) return null;
            string scope = o.TryGetProperty("scopes", out var s) && s.ValueKind == JsonValueKind.Array
                ? string.Join(' ', s.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()))
                : "";
            return (refresh, scope);
        }
        catch (JsonException) { return null; }
    }

    // The credentials file with the token response folded in; every other field stays as it was.
    public static string? ApplyRefresh(string credentialsJson, string responseJson, DateTimeOffset now)
    {
        try
        {
            if (JsonNode.Parse(credentialsJson) is not JsonObject root || root["claudeAiOauth"] is not JsonObject login) return null;
            using var doc = JsonDocument.Parse(responseJson);
            var res = doc.RootElement;
            if (res.ValueKind != JsonValueKind.Object ||
                !res.TryGetProperty("access_token", out var access) || access.ValueKind != JsonValueKind.String ||
                !res.TryGetProperty("expires_in", out var expires) || expires.ValueKind != JsonValueKind.Number) return null;

            login["accessToken"] = access.GetString();
            login["expiresAt"] = now.ToUnixTimeMilliseconds() + (long)(expires.GetDouble() * 1000);
            if (res.TryGetProperty("refresh_token", out var refresh) && refresh.ValueKind == JsonValueKind.String)
                login["refreshToken"] = refresh.GetString();
            if (res.TryGetProperty("scope", out var scope) && scope.ValueKind == JsonValueKind.String)
                login["scopes"] = new JsonArray(scope.GetString()!.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => (JsonNode?)x).ToArray());
            return root.ToJsonString();
        }
        catch (JsonException) { return null; }
    }

    // {"seven_day":{"utilization":25.0,"resets_at":"2026-11-08T14:00:00Z"},...}; utilization is 0–100.
    public static UsageResult? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("seven_day", out var week) || week.ValueKind != JsonValueKind.Object ||
                !week.TryGetProperty("utilization", out var u) || u.ValueKind != JsonValueKind.Number) return null;
            double used = u.GetDouble();
            DateTimeOffset? resets = week.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.String &&
                                     r.TryGetDateTimeOffset(out var at) ? at : null;
            return new((int)Math.Clamp(Math.Floor(used), 0, 100), resets, "", Normal);
        }
        catch (JsonException) { return null; }
    }
}

// Opens a terminal running the CLI's login. It stays open, so the outcome or an error can be read; once the
// login is saved to the credentials file, the next usage poll picks it up.
public static class CliLogin
{
    const string Command = "cmd /k claude auth login";

    public static bool Start()
    {
        foreach (var (exe, args) in new[] { ("wt.exe", Command), ("cmd.exe", "/k claude auth login") })
        {
            try
            {
                Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true })?.Dispose();
                return true;
            }
            catch (System.ComponentModel.Win32Exception ex) { Log.Info($"cli login: {exe}: {ex.GetType().Name}"); }
        }
        return false;
    }
}

// The right-click menu's way into the desktop app: claude://code/new?folder=<url-encoded path>.
public static class FolderLink
{
    public static string Build(string folder) =>
        "claude://code/new?folder=" + Uri.EscapeDataString(Path.GetFullPath(folder));

    public static void Open(string folder)
    {
        if (!Directory.Exists(folder)) { Log.Info("open-folder: not a folder"); return; }
        Process.Start(new ProcessStartInfo(Build(folder)) { UseShellExecute = true })?.Dispose();
    }
}
