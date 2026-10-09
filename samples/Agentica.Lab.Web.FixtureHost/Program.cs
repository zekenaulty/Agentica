using Agentica.Lab.Web;
using Agentica.Lab.Web.FixtureHost;

var endpoint = FixtureHostOptions.ParseEndpoint(args);
using var planners = new LoopbackFixturePlannerFactory(endpoint);
await using var app = LabWebApplication.Create(args, planners);
await app.RunAsync();
