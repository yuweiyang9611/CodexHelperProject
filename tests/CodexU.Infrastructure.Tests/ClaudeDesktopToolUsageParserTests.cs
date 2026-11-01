using System.Text.Json;
using System.Text.Json.Nodes;
using CodexU.Infrastructure;

namespace CodexU.Infrastructure.Tests;

public sealed class ClaudeDesktopToolUsageParserTests
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 11, 2, 3, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Parse_NativePairKeepsGeneralWindowsAndRealResetsOnly(bool contentIsString)
    {
        using var fixture = new NativePair(JsonSerializer.Serialize(new
        {
            plan = new
            {
                status = "ok",
                plan = "ignored-plan",
                windows = new object[]
                {
                    new { label = "5-hour limit", percentUsed = 33.5, resetsAt = "2026-11-02T09:30:00.123+02:00", resetsIn = "ignored" },
                    new { label = "Weekly · all models", percentUsed = 72, resetsAt = "2026-11-06T10:00:00Z" },
                    new { label = "Weekly · Fable", percentUsed = 91, resetsAt = "2026-11-07T10:00:00Z" }
                },
                extraUsage = new { spent = "ignored-amount" }
            },
            context = new { session = "ignored-session" }
        }), contentIsString);

        var result = ClaudeDesktopToolUsageParser.Parse(fixture.Use, fixture.Result, ObservedAt);

        Assert.True(result.Success, result.Reason);
        Assert.Null(result.Reason);
        Assert.Equal(ObservedAt, result.ObservedAt);
        Assert.Equal(33.5, result.Primary!.UsedPercent);
        Assert.Equal(300, result.Primary.WindowDurationMinutes);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 7, 30, 0, TimeSpan.Zero).AddMilliseconds(123), result.Primary.ResetsAt);
        Assert.Equal(72, result.Secondary!.UsedPercent);
        Assert.Equal(10080, result.Secondary.WindowDurationMinutes);
        Assert.Equal(new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.Zero), result.Secondary.ResetsAt);
        Assert.Equal("claude-desktop-get-usage", result.Primary.Source);
        Assert.Equal("claude-desktop-get-usage", result.Secondary.Source);
        Assert.Equal(ObservedAt, result.Primary.MeasuredAt);
        Assert.DoesNotContain("ignored-", JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-call", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("wrong-tool", "tool_mismatch")]
    [InlineData("wrong-id", "tool_mismatch")]
    [InlineData("wrong-use-type", "invalid_envelope")]
    [InlineData("wrong-result-type", "invalid_envelope")]
    [InlineData("missing-id", "invalid_envelope")]
    [InlineData("empty-id", "invalid_envelope")]
    [InlineData("tool-error", "tool_error")]
    [InlineData("invalid-error-flag", "invalid_envelope")]
    [InlineData("empty-content", "invalid_content")]
    [InlineData("object-content", "invalid_content")]
    [InlineData("image-content", "invalid_content")]
    [InlineData("multiple-texts", "ambiguous_content")]
    public void Parse_RequiresOneSuccessfulNativeResultForTheExactToolCall(string scenario, string reason)
    {
        const string payload = "{\"plan\":{\"status\":\"ok\",\"windows\":[{\"label\":\"5-hour limit\",\"percentUsed\":12}]}}";
        Action<JsonObject>? useChange = scenario switch
        {
            "wrong-tool" => node => node["name"] = "unrelated_tool",
            "wrong-use-type" => node => node["type"] = "text",
            "missing-id" => node => node.Remove("id"),
            "empty-id" => node => node["id"] = "",
            _ => null
        };
        Action<JsonObject>? resultChange = scenario switch
        {
            "wrong-id" => node => node["tool_use_id"] = "another-call",
            "wrong-result-type" => node => node["type"] = "text",
            "tool-error" => node => node["is_error"] = true,
            "invalid-error-flag" => node => node["is_error"] = "false",
            "empty-content" => node => node["content"] = new JsonArray(),
            "object-content" => node => node["content"] = new JsonObject(),
            "image-content" => node => node["content"] = new JsonArray(new JsonObject { ["type"] = "image" }),
            "multiple-texts" => node => node["content"]!.AsArray().Add(new JsonObject { ["type"] = "text", ["text"] = payload }),
            _ => null
        };
        using var fixture = new NativePair(payload, useChange: useChange, resultChange: resultChange);

        var result = ClaudeDesktopToolUsageParser.Parse(fixture.Use, fixture.Result, ObservedAt);

        Assert.False(result.Success);
        Assert.Equal(reason, result.Reason);
        Assert.Null(result.Primary);
        Assert.Null(result.Secondary);
    }

    [Theory]
    [InlineData("unavailable", "plan_unavailable")]
    [InlineData("not_applicable", "plan_not_applicable")]
    [InlineData("unknown-status", "unsupported_plan_status")]
    public void Parse_UnavailablePlanNeverBecomesAQuota(string status, string reason)
    {
        using var fixture = new NativePair(JsonSerializer.Serialize(new
        {
            plan = new { status, note = "ignored-note", windows = new[] { new { label = "5-hour limit", percentUsed = 12 } } }
        }));
        var result = ClaudeDesktopToolUsageParser.Parse(fixture.Use, fixture.Result, ObservedAt);
        Assert.False(result.Success);
        Assert.Equal(reason, result.Reason);
        Assert.Null(result.Primary);
        Assert.Null(result.Secondary);
    }

    [Theory]
    [InlineData("{\"label\":\"5-hour limit\",\"percentUsed\":-1}", "invalid_percent")]
    [InlineData("{\"label\":\"5-hour limit\",\"percentUsed\":101}", "invalid_percent")]
    [InlineData("{\"label\":\"5-hour limit\",\"percentUsed\":1e400}", "invalid_percent")]
    [InlineData("{\"label\":\"5-hour limit\",\"percentUsed\":\"12\"}", "invalid_percent")]
    [InlineData("{\"label\":\"5-hour limit\",\"percentUsed\":null}", "invalid_percent")]
    [InlineData("{\"label\":\"5-hour limit\"}", "invalid_percent")]
    [InlineData("{\"label\":\"5-hour limit\",\"percentUsed\":12,\"resetsAt\":\"2026-11-02T08:00:00\"}", "invalid_reset")]
    [InlineData("{\"label\":\"5-hour limit\",\"percentUsed\":12,\"resetsAt\":\"2026-11-02T08:00:00Z\\n\"}", "invalid_reset")]
    [InlineData("{\"label\":\"5-hour limit\",\"percentUsed\":12,\"resetsAt\":\"2026-11-02\"}", "invalid_reset")]
    [InlineData("{\"label\":\"5-hour limit\",\"percentUsed\":12,\"resetsAt\":\"in 3 hours\"}", "invalid_reset")]
    [InlineData("{\"label\":\"5-hour limit\",\"percentUsed\":12,\"resetsAt\":\"2026-02-30T08:00:00Z\"}", "invalid_reset")]
    [InlineData("{\"label\":\"5-hour limit\",\"percentUsed\":12,\"resetsAt\":123456}", "invalid_reset")]
    [InlineData("{\"percentUsed\":12}", "invalid_window")]
    [InlineData("42", "invalid_window")]
    public void Parse_InvalidWindowRejectsTheWholeMeasurement(string invalidWindow, string reason)
    {
        // A valid window before a malformed one must not leak a partial success.
        var payload = "{\"plan\":{\"status\":\"ok\",\"windows\":[{\"label\":\"Weekly · all models\",\"percentUsed\":40}," + invalidWindow + "]}}";
        using var fixture = new NativePair(payload);
        var result = ClaudeDesktopToolUsageParser.Parse(fixture.Use, fixture.Result, ObservedAt);
        Assert.False(result.Success);
        Assert.Equal(reason, result.Reason);
        Assert.Null(result.Primary);
        Assert.Null(result.Secondary);
    }

    [Theory]
    [InlineData("5-hour limit")]
    [InlineData("Weekly · all models")]
    public void Parse_DuplicateGeneralWindowLabelsAreAmbiguousEvenWhenValuesAgree(string label)
    {
        using var fixture = new NativePair(JsonSerializer.Serialize(new
        {
            plan = new { status = "ok", windows = new[] { new { label, percentUsed = 12 }, new { label, percentUsed = 12 } } }
        }));
        var result = ClaudeDesktopToolUsageParser.Parse(fixture.Use, fixture.Result, ObservedAt);
        Assert.False(result.Success);
        Assert.Equal("ambiguous_window", result.Reason);
        Assert.Null(result.Primary);
        Assert.Null(result.Secondary);
    }

    [Theory]
    [InlineData("{}", "invalid_plan")]
    [InlineData("[]", "invalid_plan")]
    [InlineData("{\"plan\":null}", "invalid_plan")]
    [InlineData("{\"plan\":{\"status\":\"ok\"}}", "invalid_windows")]
    [InlineData("{\"plan\":{\"status\":\"ok\",\"windows\":{}}}", "invalid_windows")]
    [InlineData("{\"plan\":{\"status\":\"ok\",\"windows\":[]},\"plan\":{\"status\":\"ok\",\"windows\":[]}}", "ambiguous_payload")]
    [InlineData("{\"plan\":{\"status\":\"unavailable\",\"status\":\"ok\",\"windows\":[]}}", "ambiguous_payload")]
    [InlineData("{\"plan\":{\"status\":\"ok\",\"windows\":[{\"label\":\"5-hour limit\",\"percentUsed\":0,\"percentUsed\":100}]}}", "ambiguous_payload")]
    [InlineData("not JSON", "invalid_json")]
    public void Parse_InvalidOrAmbiguousPayloadReturnsAReason(string payload, string reason)
    {
        using var fixture = new NativePair(payload);
        var result = ClaudeDesktopToolUsageParser.Parse(fixture.Use, fixture.Result, ObservedAt);
        Assert.False(result.Success);
        Assert.Equal(reason, result.Reason);
    }

    [Theory]
    [InlineData("large-ascii", "payload_too_large")]
    [InlineData("large-utf8", "payload_too_large")]
    [InlineData("deep", "invalid_json")]
    [InlineData("many-windows", "too_many_windows")]
    public void Parse_BoundsTextBytesDepthAndWindows(string scenario, string reason)
    {
        const string emptyPlan = "{\"plan\":{\"status\":\"ok\",\"windows\":[]}}";
        var payload = scenario switch
        {
            "large-ascii" => emptyPlan + new string(' ', 256 * 1024),
            "large-utf8" => "{\"plan\":{\"status\":\"ok\",\"windows\":[]},\"context\":\"" + new string('界', 90_000) + "\"}",
            "deep" => "{\"plan\":{\"status\":\"ok\",\"windows\":[]},\"context\":" + new string('[', 20) + "0" + new string(']', 20) + "}",
            "many-windows" => JsonSerializer.Serialize(new { plan = new { status = "ok", windows = Enumerable.Repeat(new { label = "Weekly · Fable", percentUsed = 12 }, 65).ToArray() } }),
            _ => throw new ArgumentException(scenario)
        };
        using var fixture = new NativePair(payload);
        var result = ClaudeDesktopToolUsageParser.Parse(fixture.Use, fixture.Result, ObservedAt);
        Assert.False(result.Success);
        Assert.Equal(reason, result.Reason);
    }

    [Fact]
    public void Parse_MissingWindowsAndNullResetsStayUnknownAndResetsInIsNeverParsed()
    {
        using var fixture = new NativePair("{\"plan\":{\"status\":\"ok\",\"windows\":[{\"label\":\"Weekly · all models\",\"percentUsed\":0,\"resetsAt\":null,\"resetsIn\":\"3d 2h\"},{\"label\":\"Weekly · Fable\",\"percentUsed\":100,\"resetsAt\":\"2026-11-09T00:00:00Z\"}]}}");
        var result = ClaudeDesktopToolUsageParser.Parse(fixture.Use, fixture.Result, ObservedAt);
        Assert.True(result.Success, result.Reason);
        Assert.Null(result.Primary);
        Assert.Equal(0, result.Secondary!.UsedPercent);
        Assert.Null(result.Secondary.ResetsAt);
    }

    [Fact]
    public void Parse_NoGeneralWindowsIsAnExplicitEmptyMeasurement()
    {
        using var fixture = new NativePair("{\"plan\":{\"status\":\"ok\",\"windows\":[{\"label\":\"Weekly · Fable\",\"percentUsed\":100}]}}");
        var result = ClaudeDesktopToolUsageParser.Parse(fixture.Use, fixture.Result, ObservedAt);
        Assert.True(result.Success, result.Reason);
        Assert.Null(result.Primary);
        Assert.Null(result.Secondary);
    }

    private sealed class NativePair : IDisposable
    {
        private readonly JsonDocument _use;
        private readonly JsonDocument _result;
        internal JsonElement Use => _use.RootElement;
        internal JsonElement Result => _result.RootElement;

        internal NativePair(string payload, bool contentIsString = false,
            Action<JsonObject>? useChange = null, Action<JsonObject>? resultChange = null)
        {
            var use = JsonSerializer.SerializeToNode(new
            {
                type = "tool_use",
                id = "fixture-call",
                name = "mcp__ccd_session_mgmt__get_usage",
                input = new { }
            })!.AsObject();
            useChange?.Invoke(use);
            _use = JsonDocument.Parse(use.ToJsonString());
            object content = contentIsString ? payload : new[] { new { type = "text", text = payload } };
            var result = JsonSerializer.SerializeToNode(new
            {
                type = "tool_result",
                tool_use_id = "fixture-call",
                is_error = false,
                content
            })!.AsObject();
            resultChange?.Invoke(result);
            _result = JsonDocument.Parse(result.ToJsonString());
        }

        public void Dispose()
        {
            _use.Dispose();
            _result.Dispose();
        }
    }
}
