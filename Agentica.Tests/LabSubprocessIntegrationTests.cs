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
    }

    [Fact]
    public async Task Quest_list_runs_as_a_real_process()
    {
        var result = await RunLabAsync("quest", "list");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("The Sun Gate (sun_gate)", result.StandardOutput, StringComparison.Ordinal);
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
