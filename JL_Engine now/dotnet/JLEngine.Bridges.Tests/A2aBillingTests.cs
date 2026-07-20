using JLEngine.Bridges.A2a;
using Microsoft.Data.Sqlite;
using Xunit;

namespace JLEngine.Bridges.Tests;

public class A2aBillingTests
{
    private static SqliteConnection NewDb()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        A2aBilling.EnsureSchema(conn);
        return conn;
    }

    [Fact]
    public void CheckAuth_NoKeysConfigured_AllowsEverything()
    {
        Environment.SetEnvironmentVariable("A2A_API_KEY", null);
        Environment.SetEnvironmentVariable("A2A_ADMIN_KEY", null);
        using var db = NewDb();
        var billing = new A2aBilling(db);

        Assert.Null(billing.CheckAuth(""));
        Assert.Null(billing.CheckAuth("anything"));
    }

    [Fact]
    public void CheckAuth_AdminKeyConfigured_RejectsUnknownKeys_AllowsAdminKey()
    {
        Environment.SetEnvironmentVariable("A2A_ADMIN_KEY", "admin-secret");
        try
        {
            using var db = NewDb();
            var billing = new A2aBilling(db);

            Assert.NotNull(billing.CheckAuth(""));
            Assert.NotNull(billing.CheckAuth("random-key"));
            Assert.Null(billing.CheckAuth("admin-secret"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("A2A_ADMIN_KEY", null);
        }
    }

    [Fact]
    public void CheckAuth_ValidAccountWithActiveStatus_IsAllowed()
    {
        Environment.SetEnvironmentVariable("A2A_ADMIN_KEY", "admin-secret");
        try
        {
            using var db = NewDb();
            var billing = new A2aBilling(db);
            billing.UpsertAccount("customer-key", subscriptionStatus: "active");

            Assert.Null(billing.CheckAuth("customer-key"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("A2A_ADMIN_KEY", null);
        }
    }

    [Fact]
    public void RateLimitExceeded_NoLimitConfigured_NeverBlocks()
    {
        Environment.SetEnvironmentVariable("A2A_MAX_REQUESTS_PER_MINUTE", null);
        using var db = NewDb();
        var billing = new A2aBilling(db);
        Assert.Null(billing.RateLimitExceeded("some-key"));
    }

    [Fact]
    public void RateLimitExceeded_TripsAfterConfiguredThreshold()
    {
        Environment.SetEnvironmentVariable("A2A_MAX_REQUESTS_PER_MINUTE", "2");
        try
        {
            using var db = NewDb();
            var billing = new A2aBilling(db);

            Assert.Null(billing.RateLimitExceeded("key1")); // 0 recorded so far
            billing.RecordUsage("key1", "task1", "message/send", 10, 20, 0, "TASK_STATE_COMPLETED");
            Assert.Null(billing.RateLimitExceeded("key1")); // 1 recorded, limit 2
            billing.RecordUsage("key1", "task2", "message/send", 10, 20, 0, "TASK_STATE_COMPLETED");
            Assert.NotNull(billing.RateLimitExceeded("key1")); // 2 recorded, limit reached
        }
        finally
        {
            Environment.SetEnvironmentVariable("A2A_MAX_REQUESTS_PER_MINUTE", null);
        }
    }

    [Fact]
    public void EstimatedPrice_MatchesJuliaFormula()
    {
        Environment.SetEnvironmentVariable("A2A_PRICE_PER_1K_REQUESTS", "1.0");
        Environment.SetEnvironmentVariable("A2A_PRICE_PER_1K_INPUT_CHARS", "0.5");
        Environment.SetEnvironmentVariable("A2A_PRICE_PER_1K_OUTPUT_CHARS", "0.25");
        Environment.SetEnvironmentVariable("A2A_PRICE_PER_TOOL_CALL", "0.1");
        try
        {
            // 1 request => 1/1000*1.0 = 0.001; 2000 chars in => 2*0.5=1.0; 1000 chars out => 1*0.25=0.25; 2 tool calls => 0.2
            var price = A2aBilling.EstimatedPrice(1, 2000, 1000, 2);
            Assert.Equal(0.001 + 1.0 + 0.25 + 0.2, price, precision: 6);
        }
        finally
        {
            Environment.SetEnvironmentVariable("A2A_PRICE_PER_1K_REQUESTS", null);
            Environment.SetEnvironmentVariable("A2A_PRICE_PER_1K_INPUT_CHARS", null);
            Environment.SetEnvironmentVariable("A2A_PRICE_PER_1K_OUTPUT_CHARS", null);
            Environment.SetEnvironmentVariable("A2A_PRICE_PER_TOOL_CALL", null);
        }
    }
}
