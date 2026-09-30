extern alias AgenticaLab;

using System.Text.Json;
using Agentica.Clients.Llm;

namespace Agentica.Tests;

public sealed class StreamTelemetryReporterTests
{
    [Fact]
    public void Jsonl_feed_reports_live_progress_terminal_usage_and_call_identity()
    {
        using var writer = new StringWriter();
        var reporter = new AgenticaLab::Agentica.Lab.Configuration.StreamTelemetryReporter(
            "gemini", jsonLines: true, includeThoughtSummaries: true,
            writer: writer);
        reporter.Report(new LlmStreamEvent(LlmStreamEventKind.Started));
        reporter.Report(new LlmStreamEvent(LlmStreamEventKind.Activity, "thought"));
        reporter.Report(new LlmStreamEvent(LlmStreamEventKind.TextDelta, "hello"));
        reporter.Report(new LlmStreamEvent(LlmStreamEventKind.ThoughtSummaryDelta,
            "Considering"));
        reporter.Report(new LlmStreamEvent(LlmStreamEventKind.TextDelta,
            new string('x', 512)));
        reporter.Report(new LlmStreamEvent(LlmStreamEventKind.Completed,
            Response: new LlmResponse("gemini", "gemini-test",
                "hello" + new string('x', 512),
                Usage: new LlmUsage(PromptTokens: 30, OutputTokens: 20,
                    ThinkingTokens: 8, TotalTokens: 50),
                FinishReason: LlmFinishReason.Stop)));

        var lines = writer.ToString().Split('\n',
            StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(6, lines.Length);
        var records = lines.Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            var roots = records.Select(record => record.RootElement).ToArray();
            var callId = roots[0].GetProperty("callId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(callId));
            Assert.All(roots, root =>
            {
                Assert.Equal(callId, root.GetProperty("callId").GetString());
                Assert.Equal("gemini", root.GetProperty("provider").GetString());
                Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
                Assert.True(root.GetProperty("elapsedMs").GetInt64() >= 0);
                Assert.True(root.TryGetProperty("at", out _));
            });
            Assert.Equal(Enumerable.Range(1, 6),
                roots.Select(root => root.GetProperty("sequence").GetInt32()));
            Assert.Equal(["started", "activity", "text_delta",
                "thought_summary_delta", "text_delta", "completed"],
                roots.Select(root => root.GetProperty("kind").GetString()));
            Assert.Equal("Considering",
                roots[3].GetProperty("thoughtSummaryDelta").GetString());
            Assert.Equal(517,
                roots[^1].GetProperty("outputCharacters").GetInt32());
            Assert.Equal(11,
                roots[^1].GetProperty("thoughtSummaryCharacters").GetInt32());
            Assert.Equal(8,
                roots[^1].GetProperty("usage").GetProperty("thinkingTokens")
                    .GetInt32());
            Assert.Equal("Stop", roots[^1].GetProperty("finishReason").GetString());
            Assert.DoesNotContain("hello", writer.ToString(),
                StringComparison.Ordinal);
            Assert.DoesNotContain(new string('x', 512), writer.ToString(),
                StringComparison.Ordinal);
        }
        finally
        {
            foreach (var record in records) record.Dispose();
        }
    }

    [Fact]
    public void Jsonl_feed_withholds_summary_text_and_resets_after_failure()
    {
        using var writer = new StringWriter();
        var reporter = new AgenticaLab::Agentica.Lab.Configuration.StreamTelemetryReporter(
            "anthropic", jsonLines: true, writer: writer);
        reporter.Report(new LlmStreamEvent(LlmStreamEventKind.Started));
        reporter.Report(new LlmStreamEvent(LlmStreamEventKind.ThoughtSummaryDelta,
            "private provider summary"));
        reporter.Report(new LlmStreamEvent(LlmStreamEventKind.Failed,
            "provider_timeout"));
        reporter.Report(new LlmStreamEvent(LlmStreamEventKind.Started));
        reporter.Report(new LlmStreamEvent(LlmStreamEventKind.Cancelled));

        var lines = writer.ToString().Split('\n',
            StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(5, lines.Length);
        Assert.DoesNotContain("private provider summary", writer.ToString(),
            StringComparison.Ordinal);
        using var first = JsonDocument.Parse(lines[0]);
        using var summary = JsonDocument.Parse(lines[1]);
        using var failure = JsonDocument.Parse(lines[2]);
        using var next = JsonDocument.Parse(lines[3]);
        using var cancellation = JsonDocument.Parse(lines[4]);
        Assert.False(summary.RootElement.TryGetProperty("thoughtSummaryDelta", out _));
        Assert.Equal(24,
            summary.RootElement.GetProperty("thoughtSummaryCharacters").GetInt32());
        Assert.Equal("provider_timeout",
            failure.RootElement.GetProperty("reason").GetString());
        Assert.NotEqual(first.RootElement.GetProperty("callId").GetString(),
            next.RootElement.GetProperty("callId").GetString());
        Assert.Equal(1, next.RootElement.GetProperty("sequence").GetInt32());
        Assert.Equal("cancelled",
            cancellation.RootElement.GetProperty("kind").GetString());
    }

    [Fact]
    public void Human_feed_only_displays_thought_summaries_when_requested()
    {
        using var withheld = new StringWriter();
        var defaultReporter = new AgenticaLab::Agentica.Lab.Configuration.StreamTelemetryReporter(
            "gemini", writer: withheld);
        defaultReporter.Report(new LlmStreamEvent(LlmStreamEventKind.Started));
        defaultReporter.Report(new LlmStreamEvent(LlmStreamEventKind.ThoughtSummaryDelta,
            "provider summary"));
        Assert.DoesNotContain("provider summary", withheld.ToString(),
            StringComparison.Ordinal);

        using var included = new StringWriter();
        var optedInReporter = new AgenticaLab::Agentica.Lab.Configuration.StreamTelemetryReporter(
            "gemini", includeThoughtSummaries: true, writer: included);
        optedInReporter.Report(new LlmStreamEvent(LlmStreamEventKind.Started));
        optedInReporter.Report(new LlmStreamEvent(LlmStreamEventKind.ThoughtSummaryDelta,
            "provider summary"));
        Assert.Contains("provider summary", included.ToString(),
            StringComparison.Ordinal);
    }
}
