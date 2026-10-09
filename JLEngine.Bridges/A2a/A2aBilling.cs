using Microsoft.Data.Sqlite;

namespace JLEngine.Bridges.A2a;

/// <summary>
/// Port of a2a_billing.jl's core metering/licensing mechanics: accounts,
/// usage ledger, rate limiting, pricing estimation, and the auth/entitlement
/// gate. Env-gated exactly like Julia — inert with no A2A_API_KEY configured.
///
/// Scope decision (explicit): this port covers the account/API-key model,
/// rate limiting, and usage-ledger recording — the mechanics that work
/// without any live third-party service. It does NOT port the Stripe
/// checkout-session / subscription-webhook lifecycle (`_a2a_prepare_checkout!`,
/// `_a2a_apply_checkout_completed!`, `_a2a_verify_stripe_signature`, etc.):
/// those require a live Stripe account and real webhook secrets to mean
/// anything, and porting the code without them would just be dead logic.
/// If real Stripe billing is needed, that's a follow-up integration against
/// the actual Stripe .NET SDK, not a mechanical port of this file.
/// </summary>
public sealed class A2aBilling(SqliteConnection db)
{
    public static void EnsureSchema(SqliteConnection db)
    {
        using var cmd1 = db.CreateCommand();
        cmd1.CommandText = """
            CREATE TABLE IF NOT EXISTS a2a_accounts (
                api_key TEXT PRIMARY KEY,
                label TEXT, plan TEXT, subscription_status TEXT, billing_email TEXT,
                stripe_customer_id TEXT, stripe_subscription_id TEXT, stripe_price_id TEXT,
                checkout_session_id TEXT, active_until TEXT,
                created_at TEXT, updated_at TEXT, last_seen_at TEXT,
                notes TEXT, metadata_json TEXT)
            """;
        cmd1.ExecuteNonQuery();

        using var cmd2 = db.CreateCommand();
        cmd2.CommandText = """
            CREATE TABLE IF NOT EXISTS a2a_usage_ledger (
                id INTEGER PRIMARY KEY,
                created_at TEXT, created_at_unix INTEGER, api_key TEXT, task_id TEXT, method TEXT,
                request_chars INTEGER, response_chars INTEGER, tool_calls INTEGER,
                price_usd REAL, status TEXT, metadata_json TEXT)
            """;
        cmd2.ExecuteNonQuery();

        using var idx1 = db.CreateCommand();
        idx1.CommandText = "CREATE INDEX IF NOT EXISTS idx_a2a_usage_key_time ON a2a_usage_ledger(api_key, created_at_unix)";
        idx1.ExecuteNonQuery();
        using var idx2 = db.CreateCommand();
        idx2.CommandText = "CREATE INDEX IF NOT EXISTS idx_a2a_accounts_status ON a2a_accounts(subscription_status)";
        idx2.ExecuteNonQuery();
    }

    private static string EnvTrim(string name, string fallback = "") => (Environment.GetEnvironmentVariable(name) ?? fallback).Trim();
    private static int EnvInt(string name, int fallback) => int.TryParse(EnvTrim(name, fallback.ToString()), out var v) ? v : fallback;
    private static double EnvFloat(string name, double fallback) => double.TryParse(EnvTrim(name, fallback.ToString()), out var v) ? v : fallback;

    public static string ApiKey => EnvTrim("A2A_API_KEY");
    public static string AdminKey => EnvTrim("A2A_ADMIN_KEY");
    public static bool AuthRequired => !string.IsNullOrEmpty(ApiKey) || !string.IsNullOrEmpty(AdminKey);
    public static int MaxRequestsPerMinute => EnvInt("A2A_MAX_REQUESTS_PER_MINUTE", 0);
    public static double PricePer1kRequests => EnvFloat("A2A_PRICE_PER_1K_REQUESTS", 0.0);
    public static double PricePer1kInputChars => EnvFloat("A2A_PRICE_PER_1K_INPUT_CHARS", 0.0);
    public static double PricePer1kOutputChars => EnvFloat("A2A_PRICE_PER_1K_OUTPUT_CHARS", 0.0);
    public static double PricePerToolCall => EnvFloat("A2A_PRICE_PER_TOOL_CALL", 0.0);

    public static bool IsAdminKey(string key) => !string.IsNullOrEmpty(key) && key == AdminKey;

    private static readonly HashSet<string> AllowedStatuses = ["active", "trialing", "", "none"];

    public bool AccountExists(string apiKey)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM a2a_accounts WHERE api_key = $key";
        cmd.Parameters.AddWithValue("$key", apiKey);
        return cmd.ExecuteScalar() is not null;
    }

    private string GetAccountStatus(string apiKey)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT subscription_status FROM a2a_accounts WHERE api_key = $key";
        cmd.Parameters.AddWithValue("$key", apiKey);
        return cmd.ExecuteScalar() as string ?? "";
    }

    public void UpsertAccount(string apiKey, string? label = null, string? plan = null, string? subscriptionStatus = null)
    {
        var key = apiKey.Trim();
        if (string.IsNullOrEmpty(key)) throw new ArgumentException("api_key is required");

        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO a2a_accounts (api_key, label, plan, subscription_status, created_at, updated_at)
            VALUES ($key, $label, $plan, $status, $now, $now)
            ON CONFLICT(api_key) DO UPDATE SET
                label = COALESCE($label, label),
                plan = COALESCE($plan, plan),
                subscription_status = COALESCE($status, subscription_status),
                updated_at = $now
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$label", (object?)label ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$plan", (object?)plan ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$status", (object?)subscriptionStatus ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Port of `_a2a_check_auth`: returns null if the request is
    /// authorized, or an error (code, message) tuple if not.</summary>
    public (int Code, string Message)? CheckAuth(string? bearerKey, bool requireAdmin = false)
    {
        if (!AuthRequired && !requireAdmin) return null;

        var key = (bearerKey ?? "").Trim();
        if (string.IsNullOrEmpty(key)) return (401, "Unauthorized — missing bearer token");
        if (requireAdmin)
        {
            return IsAdminKey(key) ? null : (403, "Forbidden — admin key required");
        }
        if (IsAdminKey(key)) return null;

        if (!AccountExists(key)) return (401, "Unauthorized — invalid or inactive API key");
        var status = GetAccountStatus(key);
        return AllowedStatuses.Contains(status.ToLowerInvariant()) ? null : (402, "Payment required — subscription inactive");
    }

    /// <summary>Port of `_a2a_rate_limit_response`: returns a (limit) tuple if
    /// the per-minute request cap is exceeded, else null.</summary>
    public int? RateLimitExceeded(string apiKey)
    {
        var limit = MaxRequestsPerMinute;
        if (limit <= 0) return null;
        var key = apiKey.Trim();
        if (string.IsNullOrEmpty(key) || IsAdminKey(key)) return null;

        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60;
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM a2a_usage_ledger WHERE api_key = $key AND created_at_unix >= $cutoff";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$cutoff", cutoff);
        var count = Convert.ToInt64(cmd.ExecuteScalar());
        return count >= limit ? limit : null;
    }

    public static double EstimatedPrice(int requestCount, int requestChars, int responseChars, int toolCalls) =>
        requestCount / 1000.0 * PricePer1kRequests +
        requestChars / 1000.0 * PricePer1kInputChars +
        responseChars / 1000.0 * PricePer1kOutputChars +
        toolCalls * PricePerToolCall;

    public void RecordUsage(string apiKey, string taskId, string method, int requestChars, int responseChars, int toolCalls, string status)
    {
        var price = EstimatedPrice(1, requestChars, responseChars, toolCalls);
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO a2a_usage_ledger
            (created_at, created_at_unix, api_key, task_id, method, request_chars, response_chars, tool_calls, price_usd, status)
            VALUES ($now, $unix, $key, $task, $method, $reqc, $resc, $tools, $price, $status)
            """;
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$unix", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$key", apiKey);
        cmd.Parameters.AddWithValue("$task", taskId);
        cmd.Parameters.AddWithValue("$method", method);
        cmd.Parameters.AddWithValue("$reqc", requestChars);
        cmd.Parameters.AddWithValue("$resc", responseChars);
        cmd.Parameters.AddWithValue("$tools", toolCalls);
        cmd.Parameters.AddWithValue("$price", price);
        cmd.Parameters.AddWithValue("$status", status);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Port of `_a2a_task_entitlement_block`: rate limit check first,
    /// then (implicitly, via CheckAuth at the call site) account status.</summary>
    public (int Code, string Message)? TaskEntitlementBlock(string apiKey)
    {
        var limit = RateLimitExceeded(apiKey);
        return limit is not null ? (429, $"Rate limit exceeded: {limit} requests/minute") : null;
    }
}
