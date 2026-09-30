extern alias AgenticaLab;

namespace Agentica.Tests;

public sealed class PlanningInputCliTests
{
    [Fact]
    public void Run_and_chat_accept_an_explicit_planning_input_ceiling()
    {
        var run = AgenticaLab::CliRunOptions.Parse(["Inspect the workspace",
            "--planner", "gemini", "--max-input-characters", "24000",
            "--context-window-tokens", "64000"]);
        var chat = AgenticaLab::ChatOptions.Parse(["--planner", "gemini",
            "--max-input-characters", "32000", "--context-window-tokens", "48000"]);

        Assert.True(run.IsValid);
        Assert.Equal(24000, run.MaxInputCharacters);
        Assert.Equal(64000, run.ContextWindowTokens);
        Assert.True(chat.IsValid);
        Assert.Equal(32000, chat.MaxInputCharacters);
        Assert.Equal(48000, chat.ContextWindowTokens);
        Assert.False(AgenticaLab::CliRunOptions.Parse(["Inspect", "--max-input-characters", "8191"])
            .IsValid);
        Assert.False(AgenticaLab::ChatOptions.Parse(["--max-input-characters", "invalid"]).IsValid);
        Assert.False(AgenticaLab::CliRunOptions.Parse(["Inspect",
            "--context-window-tokens", "8192"]).IsValid);
        Assert.True(AgenticaLab::ChatOptions.Parse(["--context-window-tokens", "8192",
            "--max-output-tokens", "1024"]).IsValid);
    }
}
