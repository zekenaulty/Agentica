using System.Diagnostics;
using Agentica.Lab.Web.Contracts;
using Agentica.Lab.Web.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace Agentica.Lab.Web.Tests;

public sealed class HostStartSdkServiceTests
{
    [Fact]
    public async Task Actual_node_sdk_accepts_typed_start_and_stops_through_real_loopback_service()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "browser-tests", "durable-start-service-probe.mjs")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var storage = Path.Combine(Path.GetTempPath(), "agentica-start-sdk", Guid.NewGuid().ToString("N"));
        var planners = new DurableStartFixture.CountingFactory();
        await using var app = LabWebApplication.Create(["--urls", "http://127.0.0.1:0"], planners, storage);
        await app.StartAsync();
        using var process = new Process
        {
            StartInfo = new("node")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = directory.FullName
            }
        };
        process.StartInfo.ArgumentList.Add("browser-tests/durable-start-service-probe.mjs");
        process.StartInfo.ArgumentList.Add(app.Urls.Single());
        try
        {
            Assert.True(process.Start());
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            var output = await stdout;
            var error = await stderr;
            Assert.True(process.ExitCode == 0, output + error);
            Assert.Contains("PASS: real SDK", output);
            Assert.Equal(1, planners.Created);
            var entry = app.Services.GetRequiredService<HostRunRegistry>().GetStart(new("sdk-host", "sdk-session", "attempt"));
            Assert.Equal("stopped", entry.Terminal!.Reason);
            Assert.Equal(1, entry.Terminal.Usage!.Steps);
        }
        finally
        {
            if (process.Id != 0 && !process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            var registry = app.Services.GetRequiredService<HostRunRegistry>();
            var entry = app.Services.GetRequiredService<HostStartStore>().Find(new("sdk-host", "sdk-session", "attempt"));
            if (entry is not null)
            {
                var run = registry.Get(entry.RunId);
                if (!run.Terminal) run.Cancel();
                await run.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await app.StopAsync(timeout.Token);
            Directory.Delete(storage, true);
        }
    }
}
