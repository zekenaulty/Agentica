using Agentica.Lab.Web;
using Agentica.Lab.Web.FixtureHost;
using Agentica.Lab.Web.Providers;

var experimentFixture = args.Contains("--experiment-fixture", StringComparer.Ordinal);
args = args.Where(arg => arg != "--experiment-fixture").ToArray();
var endpoint = FixtureHostOptions.ParseEndpoint(args);
using var planners = new LoopbackFixturePlannerFactory(endpoint);
await using var app = LabWebApplication.Create(args, planners,
    openAiConfiguration: experimentFixture ? new OpenAiProviderConfiguration(name => name == "AGENTICA_OPENAI_MODEL" ? "fixture-model" : null) : null,
    experimentExecutor: experimentFixture ? new FixtureExperimentExecutor() : null);
await app.RunAsync();
