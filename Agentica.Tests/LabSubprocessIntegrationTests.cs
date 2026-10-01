using System.Diagnostics;

namespace Agentica.Tests;

public sealed class LabSubprocessIntegrationTests
{
    [Fact]
    public async Task Missing_command_exits_with_usage_error()
    {
        var result = await RunLabAsync();

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Agentica.Lab run", result.StandardError, StringComparison.Ordinal);
        var commandUsage = result.StandardError.Split('\n').Where(line =>
            line.Contains("Agentica.Lab run ", StringComparison.Ordinal) ||
            line.Contains("Agentica.Lab chat [message]", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, commandUsage.Length);
        Assert.All(commandUsage, line =>
            Assert.Contains("--reasoning-effort", line, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Quest_list_runs_as_a_real_process()
    {
        var result = await RunLabAsync("quest", "list");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("The Sun Gate (sun_gate)", result.StandardOutput, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("chat")]
    public async Task Conflicting_reasoning_flags_fail_before_provider_configuration(string command)
    {
        var result = await RunLabAsync(command, "Inspect state", "--planner", "gemini",
            "--reasoning-effort", "high", "--thinking-budget", "off");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Choose either --reasoning-effort or --thinking-budget.",
            result.StandardError, StringComparison.Ordinal);
        Assert.Contains("none|minimal|low|medium|high|xhigh|max",
            result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("no Gemini API key", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deterministic_runtime_slice_succeeds_as_a_real_process()
    {
        var result = await RunLabAsync(
            "run",
            "Inspect the available state",
            "--planner",
            "deterministic",
            "--planning-mode",
            "stepwise");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("\"status\": \"Succeeded\"", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("\"stopReason\": \"Complete\"", result.StandardOutput, StringComparison.Ordinal);
    }

    private static async Task<ProcessResult> RunLabAsync(params string[] arguments)
    {
        var labAssembly = Path.Combine(AppContext.BaseDirectory, "Agentica.Lab.dll");
        Assert.True(File.Exists(labAssembly), $"Lab assembly was not copied beside the test host: {labAssembly}");

        var workingDirectory = Path.Combine(Path.GetTempPath(), $"agentica-lab-process-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workingDirectory);

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add(labAssembly);
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            startInfo.Environment["GEMINI_API_KEY"] = string.Empty;
            startInfo.Environment["GOOGLE_API_KEY"] = string.Empty;
            startInfo.Environment["GOOGLE_GENAI_USE_VERTEXAI"] = "false";

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start the Agentica Lab subprocess.");
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);

            return new ProcessResult(
                process.ExitCode,
                await standardOutput,
                await standardError);
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    private sealed record ProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
