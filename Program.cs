using System.Net.Http.Headers;
using System.Text.Json;

namespace YtOrgCron;

public static class Program {
    private static readonly HttpClient HttpClient = new() {
        Timeout = TimeSpan.FromMinutes(5)
    };

    public static async Task<int> Main(string[] args) {
        LoadLocalEnvFile();

        var userIdArg = ParseUserIdArgument(args);
        var startedAt = DateTime.UtcNow.ToString("o");
        var mode = userIdArg.HasValue ? $"user={userIdArg.Value}" : "all users";
        Console.WriteLine($"[ytorg-cron] Starting sync ({mode}) — {startedAt}");

        var ytAppUrl = Environment.GetEnvironmentVariable("YT_APP_URL")
            ?? Environment.GetEnvironmentVariable("yt_app_url");
        var cronSecret = Environment.GetEnvironmentVariable("CRON_SECRET")
            ?? Environment.GetEnvironmentVariable("cron_secret");

        if (string.IsNullOrWhiteSpace(ytAppUrl)) {
            Console.Error.WriteLine("[ytorg-cron] YT_APP_URL is not set. Aborting.");
            return 1;
        }

        if (string.IsNullOrWhiteSpace(cronSecret)) {
            Console.Error.WriteLine("[ytorg-cron] CRON_SECRET is not set. Aborting.");
            return 1;
        }

        var baseUri = ytAppUrl.TrimEnd('/');
        var url = userIdArg.HasValue
            ? $"{baseUri}/api/cron/sync-user/{userIdArg.Value}"
            : $"{baseUri}/api/cron/sync-all";
        var jobName = userIdArg.HasValue ? $"sync-user-{userIdArg.Value}" : "sync-all";

        const int maxRetries = 3;
        HttpResponseMessage? response = null;
        string? text = null;
        Exception? lastError = null;

        for (var attempt = 1; attempt <= maxRetries; attempt++) {
            try {
                Console.WriteLine($"[ytorg-cron] POST {url} (attempt {attempt}/{maxRetries})");

                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cronSecret);

                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                response = await HttpClient.SendAsync(request, cts.Token);
                text = await response.Content.ReadAsStringAsync(cts.Token);

                if (response.IsSuccessStatusCode) {
                    lastError = null;
                    break;
                }

                // Fail fast on 4xx client errors
                if ((int)response.StatusCode < 500) {
                    lastError = new HttpRequestException($"HTTP {(int)response.StatusCode}: {text}");
                    break;
                }

                throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {text}");
            } catch (Exception ex) {
                lastError = ex;
                if (attempt < maxRetries) {
                    var backoffSeconds = attempt * 5;
                    Console.WriteLine($"[ytorg-cron] Attempt {attempt} failed: {ex.Message}. Retrying in {backoffSeconds}s...");
                    await Task.Delay(TimeSpan.FromSeconds(backoffSeconds));
                }
            }
        }

        if (lastError != null || response == null || !response.IsSuccessStatusCode) {
            var msg = lastError?.Message ?? $"HTTP {(int)(response?.StatusCode ?? 0)}: {text}";
            Console.Error.WriteLine($"[ytorg-cron] sync failed — {msg}");
            await Db.LogCronErrorAsync(jobName, userIdArg, msg, lastError?.ToString());
            return 1;
        }

        // Process response payload
        JsonDocument doc;
        try {
            doc = JsonDocument.Parse(text ?? "{}");
        } catch (Exception ex) {
            var msg = $"Malformed JSON response: {ex.Message}";
            Console.Error.WriteLine($"[ytorg-cron] {msg}");
            await Db.LogCronErrorAsync(jobName, userIdArg, msg, text?.Length > 500 ? text[..500] : text);
            return 1;
        }

        using (doc) {
            var root = doc.RootElement;
            if (!root.TryGetProperty("results", out var resultsElem) || resultsElem.ValueKind != JsonValueKind.Array) {
                var msg = "Invalid response contract: missing or non-array 'results'";
                Console.Error.WriteLine($"[ytorg-cron] {msg}");
                await Db.LogCronErrorAsync(jobName, userIdArg, msg, text?.Length > 500 ? text[..500] : text);
                return 1;
            }

            var usersProcessed = root.TryGetProperty("usersProcessed", out var up) && up.TryGetInt32(out var uCount) ? uCount : resultsElem.GetArrayLength();
            Console.WriteLine($"[ytorg-cron] Received results for {usersProcessed} user(s)");

            var totalErrors = 0;
            foreach (var userElem in resultsElem.EnumerateArray()) {
                var uid = userElem.TryGetProperty("user_id", out var uidProp) && uidProp.TryGetInt32(out var parsedUid) ? parsedUid : (int?)null;

                // Favorites
                if (userElem.TryGetProperty("favorites", out var favElem) && favElem.ValueKind == JsonValueKind.Object) {
                    if (favElem.TryGetProperty("error", out var favErr) && !string.IsNullOrWhiteSpace(favErr.GetString())) {
                        totalErrors++;
                        Console.Error.WriteLine($"[ytorg-cron] sync-favorites error (user={uid}): {favErr.GetString()}");
                    } else {
                        var imported = GetIntOrHyphen(favElem, "imported");
                        var skipped = GetIntOrHyphen(favElem, "skipped");
                        var channels = GetIntOrHyphen(favElem, "channelsProcessed");
                        var statsUpdated = GetIntOrHyphen(favElem, "statsUpdated");
                        var subChanges = GetIntOrHyphen(favElem, "subscriberChanges");
                        Console.WriteLine($"[ytorg-cron] sync-favorites (user={uid}): imported={imported}, skipped={skipped}, channels={channels}, statsUpdated={statsUpdated}, subChanges={subChanges}");
                    }
                }

                // Followed
                if (userElem.TryGetProperty("followed", out var folElem) && folElem.ValueKind == JsonValueKind.Object) {
                    if (folElem.TryGetProperty("error", out var folErr) && !string.IsNullOrWhiteSpace(folErr.GetString())) {
                        totalErrors++;
                        Console.Error.WriteLine($"[ytorg-cron] sync-followed error (user={uid}): {folErr.GetString()}");
                    } else {
                        var imported = GetIntOrHyphen(folElem, "imported");
                        var skipped = GetIntOrHyphen(folElem, "skipped");
                        var channels = GetIntOrHyphen(folElem, "channelsProcessed");
                        var statsUpdated = GetIntOrHyphen(folElem, "statsUpdated");
                        var subChanges = GetIntOrHyphen(folElem, "subscriberChanges");
                        Console.WriteLine($"[ytorg-cron] sync-followed (user={uid}): imported={imported}, skipped={skipped}, channels={channels}, statsUpdated={statsUpdated}, subChanges={subChanges}");
                    }
                }

                // Playlists
                if (userElem.TryGetProperty("playlists", out var playElem) && playElem.ValueKind == JsonValueKind.Object) {
                    if (playElem.TryGetProperty("error", out var playErr) && !string.IsNullOrWhiteSpace(playErr.GetString())) {
                        totalErrors++;
                        Console.Error.WriteLine($"[ytorg-cron] sync-playlists error (user={uid}): {playErr.GetString()}");
                    } else {
                        var imported = GetIntOrHyphen(playElem, "imported");
                        var existing = GetIntOrHyphen(playElem, "existing");
                        var processed = GetIntOrHyphen(playElem, "playlistsProcessed");
                        var totalPlaylists = GetIntOrHyphen(playElem, "totalPlaylists");
                        Console.WriteLine($"[ytorg-cron] sync-playlists (user={uid}): imported={imported}, existing={existing}, playlists={processed}/{totalPlaylists}");
                    }
                }
            }

            var finishedAt = DateTime.UtcNow.ToString("o");
            if (totalErrors > 0) {
                Console.Error.WriteLine($"[ytorg-cron] Finished with {totalErrors} error(s) — {finishedAt}");
                return 1;
            }

            Console.WriteLine($"[ytorg-cron] Finished successfully — {finishedAt}");
            return 0;
        }
    }

    private static int? ParseUserIdArgument(string[] args) {
        for (var i = 0; i < args.Length; i++) {
            if ((args[i] == "--user-id" || args[i] == "-u") && i + 1 < args.Length) {
                if (int.TryParse(args[i + 1], out var val) && val > 0) {
                    return val;
                }
            }
        }
        return null;
    }

    private static string GetIntOrHyphen(JsonElement elem, string propName) {
        if (elem.TryGetProperty(propName, out var p)) {
            if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v)) {
                return v.ToString();
            }
            if (p.ValueKind == JsonValueKind.String) {
                return p.GetString() ?? "-";
            }
        }
        return "-";
    }

    private static void LoadLocalEnvFile() {
        var candidates = new[] {
            Path.Combine(Directory.GetCurrentDirectory(), ".env"),
            Path.Combine(AppContext.BaseDirectory, ".env"),
            Path.Combine(Directory.GetCurrentDirectory(), "../yt_app_v3/.env"),
            Path.Combine(Directory.GetCurrentDirectory(), "../ytOrg_cron/.env")
        };

        foreach (var path in candidates) {
            if (!File.Exists(path)) continue;
            try {
                foreach (var line in File.ReadAllLines(path)) {
                    var trimmed = line.Trim();
                    if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#')) continue;
                    var eq = trimmed.IndexOf('=');
                    if (eq <= 0) continue;
                    var key = trimmed[..eq].Trim();
                    var val = trimmed[(eq + 1)..].Trim().Trim('"', '\'');
                    if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key))) {
                        Environment.SetEnvironmentVariable(key, val);
                    }
                }
                break;
            } catch {
                // Ignore failure reading optional local .env
            }
        }
    }
}
