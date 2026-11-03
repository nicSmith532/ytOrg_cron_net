using System.Data.Common;
using Dapper;
using Npgsql;

namespace YtOrgCron;

public static class Db {
    public static string BuildConnectionString() {
        var dbUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
        if (!string.IsNullOrWhiteSpace(dbUrl)) {
            return NormalizeDatabaseUrl(dbUrl);
        }

        var host = Environment.GetEnvironmentVariable("DB_HOST") ?? "localhost";
        var portStr = Environment.GetEnvironmentVariable("DB_PORT") ?? "5432";
        _ = int.TryParse(portStr, out var port);
        if (port <= 0) port = 5432;

        var db = Environment.GetEnvironmentVariable("DB_NAME") ?? "postgres";
        var user = Environment.GetEnvironmentVariable("DB_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("DB_PASSWORD") ?? "";

        var csb = new NpgsqlConnectionStringBuilder {
            Host = host
            , Port = port
            , Database = db
            , Username = user
            , Password = pass
            , SearchPath = "ytl_v2,public"
            , Timeout = 10
            , CommandTimeout = 30
        };

        if (!string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) && host != "127.0.0.1") {
            csb.SslMode = SslMode.VerifyFull;
        }

        return csb.ConnectionString;
    }

    public static string NormalizeDatabaseUrl(string url) {
        var uri = new Uri(url);
        var userInfo = uri.UserInfo.Split(':');
        var username = Uri.UnescapeDataString(userInfo[0]);
        var password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "";
        var port = uri.Port > 0 ? uri.Port : 5432;
        var database = uri.AbsolutePath.TrimStart('/');

        var csb = new NpgsqlConnectionStringBuilder {
            Host = uri.Host
            , Port = port
            , Database = database
            , Username = username
            , Password = password
            , SearchPath = "ytl_v2,public"
            , Timeout = 10
            , CommandTimeout = 30
        };

        if (!string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) && uri.Host != "127.0.0.1") {
            csb.SslMode = SslMode.VerifyFull;
        }

        return csb.ConnectionString;
    }

    public static async Task LogCronErrorAsync(string jobName, int? userId, string message, string? detail = null) {
        try {
            var connStr = BuildConnectionString();
            var csb = new NpgsqlConnectionStringBuilder(connStr);
            if (csb.SslMode == SslMode.VerifyFull || csb.SslMode == SslMode.VerifyCA) {
                csb.SslMode = SslMode.Require;
            }
            await using var conn = new NpgsqlConnection(csb.ConnectionString);
            await conn.OpenAsync();
            const string sql = @"
                INSERT INTO ytl_v2.zerror_log_cron (job_name, user_id, error_message, error_detail, created_at)
                VALUES (@JobName, @UserId, @ErrorMessage, @ErrorDetail, CURRENT_TIMESTAMP)";
            await conn.ExecuteAsync(sql, new {
                JobName = jobName
                , UserId = userId
                , ErrorMessage = message
                , ErrorDetail = detail
            });
        } catch (Exception ex) {
            Console.Error.WriteLine($"[ytorg-cron] DB log failed ({ex.Message}). Original error — job={jobName} user={userId}: {message}");
            if (!string.IsNullOrWhiteSpace(detail)) {
                Console.Error.WriteLine(detail);
            }
        }
    }
}
