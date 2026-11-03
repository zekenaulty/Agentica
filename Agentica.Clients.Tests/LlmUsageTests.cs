using System.Text.Json;
using Agentica.Clients.Gemini;
using Agentica.Clients.Llm;
using Google.GenAI.Types;

namespace Agentica.Tests;

public sealed class LlmUsageTests
{
    [Fact]
    public void Existing_six_argument_constructor_and_deconstruction_preserve_raw_counts_without_assuming_inclusion()
    {
        var usage = new LlmUsage(10, 20, 5, 30, 3, 2);
        var (prompt, output, thinking, total, cached, toolUse) = usage;
        Assert.Equal<(int?, int?, int?, int?, int?, int?)>(
            (10, 20, 5, 30, 3, 2), (prompt, output, thinking, total, cached, toolUse));
        Assert.Equal(LlmOutputTokenSemantics.Unknown, usage.OutputTokenSemantics);
        Assert.Null(usage.NonThinkingOutputTokens);
        Assert.Null(usage.GeneratedOutputTokens);
        var restored = JsonSerializer.Deserialize<LlmUsage>("""{"PromptTokens":10,"OutputTokens":20,"ThinkingTokens":5,"TotalTokens":30,"CachedPromptTokens":3,"ToolUsePromptTokens":2}""");
        Assert.Equal(usage, restored);
    }

    [Theory]
    [InlineData(LlmOutputTokenSemantics.IncludesThinking, 20, 5, 30, 15, 20)]
    [InlineData(LlmOutputTokenSemantics.ExcludesThinking, 20, 5, 35, 20, 25)]
    [InlineData(LlmOutputTokenSemantics.IncludesThinking, 20, null, 30, null, 20)]
    [InlineData(LlmOutputTokenSemantics.ExcludesThinking, 20, null, 30, 20, null)]
    [InlineData(LlmOutputTokenSemantics.IncludesThinking, 0, 0, 0, 0, 0)]
    [InlineData(LlmOutputTokenSemantics.ExcludesThinking, 0, 0, 0, 0, 0)]
    [InlineData(LlmOutputTokenSemantics.IncludesThinking, 20, 20, null, 0, 20)]
    [InlineData(LlmOutputTokenSemantics.ExcludesThinking, 20, 25, null, 20, 45)]
    [InlineData(LlmOutputTokenSemantics.Unknown, 20, 5, 30, null, null)]
    [InlineData((LlmOutputTokenSemantics)999, 20, 5, 30, null, null)]
    [InlineData(LlmOutputTokenSemantics.IncludesThinking, null, 5, 30, null, null)]
    [InlineData(LlmOutputTokenSemantics.IncludesThinking, 4, 5, 30, null, null)]
    [InlineData(LlmOutputTokenSemantics.IncludesThinking, 20, 5, 19, null, null)]
    [InlineData(LlmOutputTokenSemantics.ExcludesThinking, 20, 5, 24, null, null)]
    [InlineData(LlmOutputTokenSemantics.IncludesThinking, -1, 5, 30, null, null)]
    [InlineData(LlmOutputTokenSemantics.ExcludesThinking, 20, -1, 30, null, null)]
    [InlineData(LlmOutputTokenSemantics.IncludesThinking, 20, 5, -1, null, null)]
    [InlineData(LlmOutputTokenSemantics.ExcludesThinking, int.MaxValue, 1, null, null, null)]
    [InlineData(LlmOutputTokenSemantics.ExcludesThinking, int.MaxValue, 0, null, int.MaxValue, int.MaxValue)]
    public void Derived_output_counts_use_only_known_semantics_and_available_valid_operands(
        LlmOutputTokenSemantics semantics, int? output, int? thinking, int? total,
        int? nonThinking, int? generated)
    {
        var usage = new LlmUsage(OutputTokens: output, ThinkingTokens: thinking, TotalTokens: total)
        { OutputTokenSemantics = semantics };
        Assert.Equal(nonThinking, usage.NonThinkingOutputTokens);
        Assert.Equal(generated, usage.GeneratedOutputTokens);
        Assert.Equal(output, usage.OutputTokens);
        Assert.Equal(thinking, usage.ThinkingTokens);
        Assert.Equal(total, usage.TotalTokens);
    }

    [Fact]
    public void Cached_and_tool_prompt_counts_are_preserved_without_being_added_to_generated_output()
    {
        // Anthropic's uncached input counter can be smaller than cache-read input.
        var usage = new LlmUsage(2, 20, 5, 80, 30, 23)
        { OutputTokenSemantics = LlmOutputTokenSemantics.IncludesThinking };
        Assert.Equal(15, usage.NonThinkingOutputTokens);
        Assert.Equal(20, usage.GeneratedOutputTokens);
        Assert.Equal(80, usage.TotalTokens);
        Assert.Equal(30, usage.CachedPromptTokens);
        Assert.Equal(23, usage.ToolUsePromptTokens);
        Assert.Null((usage with { PromptTokens = -1 }).GeneratedOutputTokens);
        Assert.Null((usage with { CachedPromptTokens = -1 }).NonThinkingOutputTokens);
        Assert.Null((usage with { ToolUsePromptTokens = -1 }).GeneratedOutputTokens);
    }

    [Fact]
    public void Completed_stream_telemetry_preserves_usage_semantics_without_inventing_live_token_counts()
    {
        using var feed = new LlmTelemetryFeed("fixture");
        feed.Report(new(LlmStreamEventKind.TextDelta, "Text length is not tokens."));
        Assert.Null(feed.Latest!.Usage);
        var usage = new LlmUsage(10, 20, 5, 30)
        { OutputTokenSemantics = LlmOutputTokenSemantics.IncludesThinking };
        feed.Report(new(LlmStreamEventKind.Completed, Response: new LlmResponse("fixture", "model", "answer", Usage: usage)));
        Assert.Same(usage, feed.LastTerminal!.Usage);
        var restored = JsonSerializer.Deserialize<LlmUsage>(JsonSerializer.Serialize(feed.LastTerminal.Usage));
        Assert.Equal(usage, restored);
        Assert.Equal(15, restored!.NonThinkingOutputTokens);
        Assert.Equal(20, restored.GeneratedOutputTokens);
    }

    [Fact]
    public void Gemini_SDK_mapper_keeps_source_categories_and_marks_candidates_as_excluding_thoughts()
    {
        var native = new GenerateContentResponse
        {
            Candidates = [new() { Content = new() { Role = "model", Parts = [new() { Text = "answer" }] } }],
            UsageMetadata = new()
            {
                PromptTokenCount = 11, CandidatesTokenCount = 2, ThoughtsTokenCount = 3,
                TotalTokenCount = 20, CachedContentTokenCount = 1, ToolUsePromptTokenCount = 4
            }
        };
        var response = GeminiResponseMapper.Map("fixture-model", native);
        Assert.Equal("answer", response.Text);
        Assert.Equal(new LlmUsage(11, 2, 3, 20, 1, 4)
        { OutputTokenSemantics = LlmOutputTokenSemantics.ExcludesThinking }, response.Usage);
        Assert.Equal(2, response.Usage!.NonThinkingOutputTokens);
        Assert.Equal(5, response.Usage.GeneratedOutputTokens);
    }
}
