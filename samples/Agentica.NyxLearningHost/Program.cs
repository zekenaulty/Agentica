using Agentica.NyxLearningHost;
using System.Text.Json;

var results = new[]
{
    await LearningExperiment.RunAsync(LearningScenario.ExactSource),
    await LearningExperiment.RunAsync(LearningScenario.EffectRecovery),
    await LearningExperiment.RunAsync(LearningScenario.ExactSource, new(MirrorAvailable: false)),
    await LearningExperiment.RunAsync(LearningScenario.EffectRecovery, new(ReconcileKnown: false)),
    await LearningExperiment.RunAsync(LearningScenario.EffectRecovery, new(InertShadow: true))
};
Console.WriteLine(JsonSerializer.Serialize(results.Select(result => new
{
    result.Scenario,
    result.InertShadow,
    result.EvidenceLevel,
    result.MatchesReference,
    satisfaction = result.Fulfillment.Satisfaction.ToString(),
    acceptance = result.Acceptance.Status.ToString(),
    runtime = result.Outcome?.Outcome.Status.ToString(),
    result.Metrics,
    result.InvokedTools
}), new JsonSerializerOptions { WriteIndented = true }));
return results.All(result => result.MatchesReference) ? 0 : 1;

