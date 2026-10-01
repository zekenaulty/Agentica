extern alias AgenticaLab;

using Agentica.Clients.Llm;

namespace Agentica.Tests;

public sealed class ReasoningEffortCliTests
{
    [Theory]
    [InlineData("none", LlmReasoningEffort.None)]
    [InlineData("minimal", LlmReasoningEffort.Minimal)]
    [InlineData("low", LlmReasoningEffort.Low)]
    [InlineData("medium", LlmReasoningEffort.Medium)]
    [InlineData("high", LlmReasoningEffort.High)]
    [InlineData("xhigh", LlmReasoningEffort.XHigh)]
    [InlineData("max", LlmReasoningEffort.Max)]
    [InlineData("XHigh", LlmReasoningEffort.XHigh)]
    public void Run_and_chat_preserve_named_effort_and_summary_preference(
        string value, LlmReasoningEffort expected)
    {
        var run = AgenticaLab::CliRunOptions.Parse(["Inspect", "--planner", "openai",
            "--reasoning-effort", value, "--include-thoughts"]);
        var chat = AgenticaLab::ChatOptions.Parse(["--planner", "openai",
            "--reasoning-effort", value, "--include-thoughts"]);

        Assert.True(run.IsValid, run.Error);
        Assert.True(chat.IsValid, chat.Error);
        Assert.Equal(expected, run.ReasoningEffort);
        Assert.Equal(expected, chat.ReasoningEffort);
        Assert.True(chat.IncludeThoughts);
        Assert.Null(run.ThinkingBudget);
        Assert.Null(chat.ThinkingBudget);
        Assert.Equal(LlmThinkingOptions.AtEffort(expected, includeThoughts: true),
            run.CreateThinkingOptions());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("7")]
    [InlineData("ultra")]
    [InlineData("low,high")]
    [InlineData("")]
    public void Effort_requires_a_documented_name(string value)
    {
        var run = AgenticaLab::CliRunOptions.Parse(["Inspect", "--reasoning-effort", value]);
        var chat = AgenticaLab::ChatOptions.Parse(["--reasoning-effort", value]);
        Assert.False(run.IsValid);
        Assert.False(chat.IsValid);
        Assert.Contains("--reasoning-effort", run.Error, StringComparison.Ordinal);
        Assert.Contains("--reasoning-effort", chat.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_effort_value_is_an_option_error()
    {
        Assert.False(AgenticaLab::CliRunOptions.Parse(["Inspect", "--reasoning-effort"]).IsValid);
        Assert.False(AgenticaLab::ChatOptions.Parse(["--reasoning-effort", "--include-thoughts"]).IsValid);
    }

    [Theory]
    [InlineData("dynamic", -1)]
    [InlineData("off", 0)]
    [InlineData("0", 0)]
    [InlineData("4096", 4096)]
    public void Existing_budget_selection_keeps_its_mapping(string value, int tokens)
    {
        var run = AgenticaLab::CliRunOptions.Parse(["Inspect", "--thinking-budget", value,
            "--include-thoughts"]);
        var chat = AgenticaLab::ChatOptions.Parse(["--thinking-budget", value]);
        Assert.True(run.IsValid, run.Error);
        Assert.True(chat.IsValid, chat.Error);
        Assert.Null(run.ReasoningEffort);
        Assert.Null(chat.ReasoningEffort);
        Assert.Equal(value, chat.ThinkingBudget);
        Assert.Equal(new LlmThinkingOptions(tokens, IncludeThoughts: true),
            run.CreateThinkingOptions());
    }

    [Fact]
    public void Omitted_controls_preserve_provider_defaults()
    {
        Assert.Null(AgenticaLab::CliRunOptions.Parse(["Inspect"]).CreateThinkingOptions());
        Assert.Equal(new LlmThinkingOptions(IncludeThoughts: true),
            AgenticaLab::CliRunOptions.Parse(["Inspect", "--include-thoughts"]).CreateThinkingOptions());
    }

    [Theory]
    [InlineData("dynamic")]
    [InlineData("off")]
    [InlineData("4096")]
    public void Explicit_budget_and_effort_are_rejected_in_either_order(string budget)
    {
        var run = AgenticaLab::CliRunOptions.Parse(["Inspect", "--thinking-budget", budget,
            "--reasoning-effort", "high"]);
        var chat = AgenticaLab::ChatOptions.Parse(["--reasoning-effort", "high",
            "--thinking-budget", budget]);
        Assert.False(run.IsValid);
        Assert.False(chat.IsValid);
        Assert.Equal("Choose either --reasoning-effort or --thinking-budget.", run.Error);
        Assert.Equal(run.Error, chat.Error);
    }
}
