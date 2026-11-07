using System.Net;
using System.Text;
using System.Text.Json;
using Agentica.Lab.Web.Chat;

namespace Agentica.ProviderLiveProof;

/// <summary>Provider-free three-phase proof of the real Lab planner/native replay boundary.
/// Fake count results qualify flow only, never a model's real token allowance.</summary>
internal static class HarnessBudgetSelfTest
{
    internal static async Task RunAsync(string ledgerPath)
    {
        var root = Path.GetDirectoryName(ledgerPath)!;
        var scratch = Path.Combine(root, "scratch");
        Directory.CreateDirectory(scratch);
        const string marker = "0123456789abcdef0123456789abcdef";
        const int left = 1234, right = 5678;
        var challenge = JsonSerializer.Serialize(new { marker, left, right });
        File.WriteAllText(Path.Combine(scratch, ScenarioRunner.ChallengeFile), challenge);
        var bodies = new List<string>();
        var traces = new List<ExperimentTraceInput>();
        using var handler = new HarnessHandler(bodies);
        using var guard = new DurableProviderGuard(ledgerPath, handler);
        using var http = new HttpClient(guard) { Timeout = Timeout.InfiniteTimeSpan };
        using var inner = new OpenAiExperimentExecutor(new(name => name == "OPENAI_API_KEY" ? "fixture-key" : null), http);
        var executor = new LinkedCountExecutor(inner, guard);
        var settings = ScenarioRunner.Settings();
        using var counter = (IDisposable)executor.CreateInputTokenCounter(settings);
        var turn = new ExperimentTurn("fixture-turn", "fixture-chat", 1, ScenarioRunner.Prompt, "", "running",
            DateTimeOffset.UtcNow, null, 1, settings, "transcript-only");
        var context = new ExperimentExecutionContext("fixture-chat", turn, [], scratch,
            item => { traces.Add(item); return Task.CompletedTask; }, _ => Task.CompletedTask)
        { InputTokenCounter = (IExperimentInputTokenCounter)counter };
        var result = await executor.ExecuteAsync(context, CancellationToken.None).ConfigureAwait(false);
        if (result.Status != "completed" || result.AssistantText != marker + ":" + ((long)left * right).ToString(System.Globalization.CultureInfo.InvariantCulture))
            throw new InvalidOperationException("proof.offline_harness_not_accepted:" + result.ErrorCode);
        var outcome = JsonSerializer.SerializeToElement(traces.Single(item => item.Type == "harness.outcome").Data);
        if (outcome.GetProperty("receipts").GetArrayLength() != 3 || outcome.GetProperty("refinements").GetArrayLength() != 2 ||
            outcome.GetProperty("residualObligations").GetArrayLength() != 0 || bodies.Count != 3)
            throw new InvalidOperationException("proof.offline_harness_causality_missing");
        var sizes = bodies.Select(body => body.Length).ToArray();
        var inputLengths = bodies.Select(body => { using var json = JsonDocument.Parse(body); return json.RootElement.GetProperty("input").GetArrayLength(); }).ToArray();
        if (sizes.Any(size => size > settings.MaxInputCharacters) || inputLengths[1] <= 1 || inputLengths[2] != 1)
            throw new InvalidOperationException("proof.offline_harness_replay_did_not_reset:" + string.Join(",", sizes) + ";" + string.Join(",", inputLengths));
        Console.WriteLine("Offline three-phase harness wire characters: " + string.Join(",", sizes) + "; input item counts: " + string.Join(",", inputLengths));
    }

    private sealed class HarnessHandler(List<string> bodies) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var text = await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (request.RequestUri!.AbsolutePath.EndsWith("/input_tokens", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = new StringContent("{\"object\":\"response.input_tokens\",\"input_tokens\":1024}", Encoding.UTF8, "application/json") };
            bodies.Add(text);
            var phase = bodies.Count;
            if (phase > 3) throw new InvalidOperationException("proof.unexpected_offline_repair");
            if (phase > 1 && !text.Contains("scratch.read", StringComparison.Ordinal)) throw new InvalidOperationException("proof.scratch_evidence_missing");
            if (phase == 2 && (!text.Contains("1234", StringComparison.Ordinal) || !text.Contains("5678", StringComparison.Ordinal)))
                throw new InvalidOperationException("proof.operands_not_observed");
            if (phase == 3 && (!text.Contains("0123456789abcdef0123456789abcdef", StringComparison.Ordinal) || !text.Contains("7006652", StringComparison.Ordinal)))
                throw new InvalidOperationException("proof.required_evidence_lost_after_native_reset");
            var tool = phase == 1 ? ExperimentToolIds.ScratchRead : phase == 2 ? ExperimentToolIds.Calculate : OpenAiExperimentExecutor.ResponseToolId;
            object input = phase == 1 ? new { path = ScenarioRunner.ChallengeFile } : phase == 2 ? new { expression = "1234*5678" } : new { content = "0123456789abcdef0123456789abcdef:7006652" };
            var plan = new
            {
                planId = "plan-" + phase,
                description = "A bounded slice based on currently observed public evidence.",
                steps = new[] { new { stepId = "step-" + phase, toolId = tool,
                    kind = phase < 3 ? "query" : "action", effect = phase < 3 ? "readOnly" : "writesLocalState", input,
                    reason = phase == 1 ? "Read the missing challenge." : phase == 2 ? "Compute the exact product from the observed scratch content." : "Emit the exact answer supported by the observed scratch marker and calculation result.",
                    intent = new { action = "Execute the next bounded operation.", rationale = "The latest public evidence establishes this operation's inputs.", expectedOutcome = "A receipt and authoritative result for the next planning decision." } } }
            };
            object output = phase == 1 ? plan : new { fromPlanId = "plan-" + (phase - 1), reason = "observation", evidence = Array.Empty<object>(), refinedPlan = plan };
            var json = JsonSerializer.Serialize(output);
            var terminal = new
            {
                type = "response.completed",
                response = new
                {
                    id = "fixture-response-" + phase,
                    status = "completed",
                    output = new object[] { new { type = "reasoning", encrypted_content = new string('x', 600) },
                    new { type = "message", role = "assistant", content = new[] { new { type = "output_text", text = json } } } },
                    usage = new { input_tokens = 1024, output_tokens = 256, output_tokens_details = new { reasoning_tokens = 32 } }
                }
            };
            return new(HttpStatusCode.OK) { Content = new StringContent("data: " + JsonSerializer.Serialize(terminal) + "\n\n", Encoding.UTF8, "text/event-stream") };
        }
    }
}
