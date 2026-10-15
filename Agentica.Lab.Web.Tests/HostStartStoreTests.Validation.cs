using Agentica.Lab.Web.Contracts;
using Agentica.Lab.Web.Providers;
using Agentica.Lab.Web.Runtime;

namespace Agentica.Lab.Web.Tests;

public sealed partial class HostStartStoreTests
{
    [Theory]
    [InlineData("epoch")]
    [InlineData("scope")]
    [InlineData("perspective")]
    [InlineData("objective-id")]
    [InlineData("objective")]
    [InlineData("observation")]
    [InlineData("observation-id")]
    [InlineData("observation-time")]
    [InlineData("observation-revision")]
    [InlineData("facts")]
    [InlineData("capabilities")]
    [InlineData("provider")]
    [InlineData("limits")]
    public void ChangedOriginalAttemptCannotBeAdmittedEvenWithMatchingAcceptedInput(string change)
    {
        var store = new HostStartStore(_root);
        Admit(store);
        var before = Bytes();
        var changed = Change(Request(), change);
        Error("start.changed", () => store.Admit("attempt", changed, changed, "run"));
        Assert.Equal(before, Bytes());
    }

    [Theory]
    [InlineData("host")]
    [InlineData("session")]
    [InlineData("epoch")]
    [InlineData("scope")]
    [InlineData("perspective")]
    [InlineData("objective-id")]
    [InlineData("objective")]
    [InlineData("observation")]
    [InlineData("observation-id")]
    [InlineData("observation-time")]
    [InlineData("observation-revision")]
    [InlineData("facts")]
    [InlineData("capabilities")]
    [InlineData("provider")]
    [InlineData("limits")]
    public void AcceptedRequestCannotChangeOriginalMeaningOrBudget(string change)
    {
        var store = new HostStartStore(_root);
        var before = Bytes();
        Error("start.accepted_changed", () => store.Admit("attempt", Request(), Change(Request(), change), "run"));
        Assert.Equal(before, Bytes());
        Assert.Null(store.Find(Key()));
    }

    [Theory]
    [InlineData(" GOOGLE ", "gemini")]
    [InlineData("Claude", "anthropic")]
    [InlineData(" xAI", "grok")]
    [InlineData("OPENAI", "openai")]
    public void NormalizationCanFreezeOnlyUnspecifiedProviderChoicesAndMaterializeLimits(string requested, string normalized)
    {
        var store = new HostStartStore(_root);
        var original = Request() with { Provider = new ProviderSettings(requested), Limits = null };
        var accepted = original with { Provider = original.Provider with { Provider = normalized, Model = "configured-model",
            ThinkingEffort = "high", ThinkingEffortResolved = true }, Limits = new HostRunLimits() };
        var entry = store.Admit("attempt", original, accepted, "run");
        Assert.NotEqual(entry.OriginalRequestHash, entry.AcceptedRequestHash);
        Same(entry, new HostStartStore(_root).Find(Key())!);
        var before = Bytes();
        Error("start.changed", () => store.Admit("attempt", original, accepted with { Provider = accepted.Provider with { Model = "new-default" } }, "run"));
        Error("start.changed", () => store.Admit("attempt", original with { Provider = original.Provider with { Provider = normalized } }, accepted, "run"));
        Assert.Equal(before, Bytes());
    }

    [Fact]
    public void NullProviderMayMaterializeDefaultsButExplicitDefaultsCannotBeRemoved()
    {
        var store = new HostStartStore(_root);
        var original = Request() with { Provider = null, Limits = null };
        var accepted = original with { Provider = new ProviderSettings(), Limits = new HostRunLimits() };
        var entry = store.Admit("attempt", original, accepted, "run");
        Assert.Null(entry.OriginalRequest.Provider);
        Assert.Null(entry.OriginalRequest.Limits);
        Assert.NotNull(entry.AcceptedRequest.Provider);
        Error("start.changed", () => store.Admit("attempt", accepted, accepted, "run"));
        Error("start.accepted_changed", () => store.Admit("another", accepted, original, "another-run"));
        Error("start.accepted_changed", () => store.Admit("another", Request(), Request() with { Limits = null }, "another-run"));
    }

    [Theory]
    [InlineData("model")]
    [InlineData("thinking")]
    [InlineData("tokens")]
    [InlineData("context")]
    [InlineData("summaries")]
    [InlineData("api")]
    [InlineData("resolved-null")]
    [InlineData("unresolve")]
    public void ProviderNormalizationCannotOverrideExplicitControls(string change)
    {
        var original = Request();
        if (change is "resolved-null" or "unresolve")
            original = original with { Provider = original.Provider! with { ThinkingEffort = null, ThinkingEffortResolved = true } };
        var provider = original.Provider!;
        var changed = change switch
        {
            "model" => provider with { Model = "other" },
            "thinking" => provider with { ThinkingEffort = "high" },
            "tokens" => provider with { MaxOutputTokens = 2048 },
            "context" => provider with { ContextWindowTokens = 8192 },
            "summaries" => provider with { IncludeThoughtSummaries = true },
            "api" => provider with { GeminiApi = "other" },
            "resolved-null" => provider with { ThinkingEffort = "high" },
            _ => provider with { ThinkingEffortResolved = false }
        };
        var store = new HostStartStore(_root);
        var before = Bytes();
        Error("start.accepted_changed", () => store.Admit("attempt", original, original with { Provider = changed }, "run"));
        Assert.Equal(before, Bytes());
    }

    [Fact]
    public void AttemptRunObjectiveAndOpenSessionCollisionsCannotCreateDuplicates()
    {
        var store = new HostStartStore(_root);
        var entry = Admit(store);
        var other = Request() with { ObjectiveId = "other" };
        Error("start.changed", () => store.Admit("attempt", Request(), Request(), "other-run"));
        Error("start.run_collision", () => store.Admit("other", other, other, "run"));
        Error("start.objective_retained", () => store.Admit("other", Request(), Request(), "other-run"));
        Error("start.session_open", () => store.Admit("other", other, other, "other-run"));
        var foreign = Request() with { HostId = "other" };
        Error("start.run_collision", () => store.Admit("attempt", foreign, foreign, "run"));
        store.Admit("attempt", foreign, foreign, "foreign-run");
        store.RequestStop(Reference(entry));
        store.SettleStopped(Reference(entry), "interrupted", null, true);
        Error("start.objective_retained", () => store.Admit("new-attempt", Request(), Request(), "new-run"));
        store.Admit("new-attempt", other, other, "new-run");
        Assert.True(store.HasOpen("host", "session"));
        Assert.True(store.IsStopRequested("host", "session", "objective"));
        Assert.False(store.IsStopRequested("host", "session", "other"));
    }

    [Theory]
    [InlineData("host", "start.not_found")]
    [InlineData("session", "start.not_found")]
    [InlineData("attempt", "start.not_found")]
    [InlineData("run", "start.reference")]
    [InlineData("hash", "start.reference")]
    public void StopAndSettlementRequireEveryOriginalReferenceField(string change, string code)
    {
        var store = new HostStartStore(_root);
        var entry = Admit(store);
        var reference = Reference(entry);
        var wrong = change switch
        {
            "host" => reference with { HostId = "other" },
            "session" => reference with { SessionId = "other" },
            "attempt" => reference with { AttemptId = "other" },
            "run" => reference with { RunId = "other" },
            _ => reference with { AcceptedRequestHash = "sha256:" + new string('0', 64) }
        };
        var before = Bytes();
        Error(code, () => store.RequestStop(wrong));
        Assert.Equal(before, Bytes());
        store.RequestStop(reference);
        before = Bytes();
        Error(code, () => store.SettleStopped(wrong, "interrupted", null, true));
        Assert.Equal(before, Bytes());
        Assert.True(store.HasOpen("host", "session"));
    }

    [Theory]
    [InlineData("reason")]
    [InlineData("stopped-null")]
    [InlineData("stopped-unknown")]
    [InlineData("interrupted-known")]
    [InlineData("interrupted-usage")]
    [InlineData("steps")]
    [InlineData("refinements")]
    [InlineData("continuations")]
    [InlineData("calls")]
    public void InvalidTerminalEvidenceCannotClearOpenFence(string change)
    {
        var store = new HostStartStore(_root);
        var entry = Admit(store);
        store.RequestStop(Reference(entry));
        var before = Bytes();
        var reason = change == "reason" ? "completed" : change.StartsWith("interrupted", StringComparison.Ordinal) ? "interrupted" : "stopped";
        HostRunUsage? usage = change is "stopped-null" or "interrupted-known" ? null : new HostRunUsage();
        usage = change switch
        {
            "steps" => usage! with { Steps = -1 },
            "refinements" => usage! with { Refinements = -1 },
            "continuations" => usage! with { Continuations = -1 },
            "calls" => usage! with { ProviderCalls = -1 },
            _ => usage
        };
        Error(change == "reason" ? "start.reason" : "start.usage", () => store.SettleStopped(Reference(entry), reason, usage,
            change is "stopped-unknown" or "interrupted-usage"));
        Assert.Equal(before, Bytes());
        Assert.True(store.HasOpen("host", "session"));
    }

    [Fact]
    public void KnownUsageCannotBeReplacedAfterSettlement()
    {
        var store = new HostStartStore(_root);
        var entry = Admit(store);
        store.RequestStop(Reference(entry));
        store.SettleStopped(Reference(entry), "stopped", new HostRunUsage(1, 2, 3, 4), false);
        var before = Bytes();
        Error("start.terminal_changed", () => store.SettleStopped(Reference(entry), "stopped", new HostRunUsage(1, 2, 3, 5), false));
        Error("start.terminal_changed", () => store.SettleStopped(Reference(entry), "interrupted", null, true));
        Assert.Equal(before, Bytes());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("bad\nidentity")]
    public void InvalidIdentitiesCannotQueryOrMutateTheStore(string invalid)
    {
        var store = new HostStartStore(_root);
        var entry = Admit(store);
        var before = Bytes();
        Error("input.invalid", () => store.Find(Key() with { AttemptId = invalid }));
        Error("input.invalid", () => store.HasOpen(invalid, "session"));
        Error("input.invalid", () => store.HasRetainedObjective("host", "session", invalid));
        Error("input.invalid", () => store.IsStopRequested("host", invalid, "objective"));
        Error("input.invalid", () => store.Admit(invalid, Request(), Request(), "new-run"));
        Error("input.invalid", () => store.RequestStop(Reference(entry) with { RunId = invalid }));
        Error("input.invalid", () => store.SettleStopped(Reference(entry) with { AttemptId = invalid }, "interrupted", null, true));
        Assert.Equal(before, Bytes());
    }

    [Fact]
    public void CanonicalObjectKeyOrderingDoesNotCreateAChangedAttempt()
    {
        var store = new HostStartStore(_root);
        var original = Request();
        original = original with { Observation = original.Observation with { Data = HostProtocol.Element(new { position = "north", revision = 7 }) } };
        var accepted = original with { Observation = original.Observation with { Data = HostProtocol.Element(new { revision = 7, position = "north" }) } };
        var entry = store.Admit("attempt", original, accepted, "run");
        var before = Bytes();
        Assert.Equal(entry.OriginalRequestHash, entry.AcceptedRequestHash);
        Same(entry, store.Admit("attempt", accepted, original, "run"));
        Assert.Equal(before, Bytes());
    }

    private static HostRunRequest Change(HostRunRequest request, string change) => change switch
    {
        "host" => request with { HostId = "other" },
        "session" => request with { SessionId = "other" },
        "epoch" => request with { SessionEpoch = "other" },
        "scope" => request with { ScopeId = "other" },
        "perspective" => request with { PerspectiveId = "other" },
        "objective-id" => request with { ObjectiveId = "other" },
        "objective" => request with { Objective = "Different objective" },
        "observation" => request with { Observation = request.Observation with { Data = HostProtocol.Element(new { position = "south" }) } },
        "observation-id" => request with { Observation = request.Observation with { ObservationId = "changed" } },
        "observation-time" => request with { Observation = request.Observation with { ObservedAt = request.Observation.ObservedAt.AddTicks(1) } },
        "observation-revision" => request with { Observation = request.Observation with { Revision = 8 } },
        "facts" => request with { Observation = request.Observation with { Facts = [request.Observation.Facts![0] with { Summary = "changed fact" }] } },
        "capabilities" => request with { Capabilities = [request.Capabilities[0] with { Description = "Different manifest" }] },
        "provider" => request with { Provider = request.Provider! with { MaxOutputTokens = 2048 } },
        _ => request with { Limits = request.Limits! with { MaxSteps = 16 } }
    };
}
