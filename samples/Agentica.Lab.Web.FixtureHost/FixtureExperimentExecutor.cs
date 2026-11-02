using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentica.Lab.Web.Chat;

namespace Agentica.Lab.Web.FixtureHost;

/// <summary>Explicit browser fixture only. Never reads credentials or makes network calls.</summary>
public sealed class FixtureExperimentExecutor : IExperimentExecutor
{
    public async Task<ExperimentExecutionResult> ExecuteAsync(ExperimentExecutionContext context, CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object?> { ["model"] = context.Turn.Settings.Model, ["store"] = false, ["stream"] = true,
            ["input"] = context.History.Select(m => new { role = m.Role, content = m.Content }).Append(new { role = "user", content = context.Turn.UserText }) };
        if (context.Turn.Settings.Instructions.Length > 0) body["instructions"] = context.Turn.Settings.Instructions;
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body))));
        await context.Trace(new("provider.request", new { body, hash, coverage = "synthetic-browser-fixture; no provider dispatch" }, "fixture-call"));
        await context.Trace(new("provider.record", new { type = "response.output_item.added", output_index = 0,
            item = new { id = "fixture-reasoning", type = "reasoning", status = "in_progress" } }, "fixture-call", "fixture-reasoning"));
        await context.Trace(new("provider.item", new { type = "response.output_item.added", item = new { id = "fixture-reasoning", type = "reasoning", status = "in_progress" } }, "fixture-call", "fixture-reasoning"));
        await context.Trace(new("provider.summary.delta", new { text = "Fixture reasoning summary." }, "fixture-call", "fixture-reasoning"));
        await context.Output("Fixture answer: ");
        await Task.Delay(400, cancellationToken);
        await context.Output(context.Turn.UserText);
        await Task.Delay(400, cancellationToken);
        await context.Trace(new("provider.item", new { type = "response.completed", response = new { id = "fixture-response", status = "completed" } }, "fixture-call"));
        return new("completed", Usage: new { providerCalls = 0, fixtureCalls = 1, inputTokens = 20, outputTokens = 12, reasoningTokens = 4 });
    }
}
