using Agentica.Observations;
using Agentica.Events;
using Agentica.Execution;
using Agentica.Outcomes;
using Agentica.Planning;
using Agentica.Projection;
using Agentica.Requests;
using Agentica.Tools;
using System.Text.Json;

namespace Agentica.Tests;

public sealed class SemanticProjectionTests
{
    [Fact]
    public void Recursive_projection_inherits_perspective_facets_and_keeps_foreign_scope_out()
    {
        var catalog = Catalog();
        var query = Query(["concept.workflow"], maxDepth: 4);

        var projection = new SemanticProjectionCompiler(catalog).Compile(query);

        Assert.Equal(["concept.workflow", "concept.task"],
            projection.Nodes.Select(node => node.NodeId));
        Assert.Equal([0, 1], projection.Nodes.Select(node => node.Depth));
        Assert.Equal(["relation.depends", "relation.recurs"],
            projection.Relations.Select(relation => relation.RelationId));
        Assert.Equal("task.graph.build", Assert.Single(projection.Bindings).TargetId);
        Assert.Equal("Use a bounded task graph", projection.Nodes[0].Facets[SemanticFacets.How].Text);
        Assert.Equal("builder", projection.Nodes[0].FacetSources[SemanticFacets.How]);
        Assert.Equal("Frame the objective", projection.Nodes[0].Facets[SemanticFacets.Why].Text);
        Assert.Equal("general", projection.Nodes[0].FacetSources[SemanticFacets.Why]);
        Assert.False(projection.Nodes[1].Facets.ContainsKey(SemanticFacets.How));
        Assert.DoesNotContain("beta.secret", JsonSerializer.Serialize(projection), StringComparison.Ordinal);
        Assert.DoesNotContain("beta.private.task", JsonSerializer.Serialize(projection), StringComparison.Ordinal);

        var second = new SemanticProjectionCompiler(Catalog(reverse: true)).Compile(query);
        Assert.Equal(projection.ProjectionHash, second.ProjectionHash);
        Assert.Equal(projection.ScopedSourceHash, second.ScopedSourceHash);
    }

    [Fact]
    public void Hidden_domain_changes_do_not_change_scoped_hash_or_leak_through_seed_errors()
    {
        var baseline = new SemanticProjectionCompiler(Catalog()).Compile(Query(["concept.workflow"]));
        var changed = new SemanticProjectionCompiler(Catalog(hiddenText: "new private value"))
            .Compile(Query(["concept.workflow"]));

        Assert.Equal(baseline.ScopedSourceHash, changed.ScopedSourceHash);
        Assert.Equal(baseline.ProjectionHash, changed.ProjectionHash);
        var error = Assert.Throws<ArgumentException>(() =>
            new SemanticProjectionCompiler(Catalog()).Compile(Query(["beta.secret"])));
        Assert.DoesNotContain("beta.secret", error.Message, StringComparison.Ordinal);
        var hiddenPerspective = Assert.Throws<UnauthorizedAccessException>(() =>
            new SemanticProjectionCompiler(Catalog()).Compile(
                Query(["concept.workflow"]) with { PerspectiveId = "beta-view" }));
        var absentPerspective = Assert.Throws<UnauthorizedAccessException>(() =>
            new SemanticProjectionCompiler(Catalog()).Compile(
                Query(["concept.workflow"]) with { PerspectiveId = "missing-view" }));
        Assert.Equal(hiddenPerspective.Message, absentPerspective.Message);
    }

    [Fact]
    public void Vector_inspection_ranks_matching_model_and_rejects_dimension_mismatch()
    {
        var catalog = Catalog();
        var compiler = new SemanticProjectionCompiler(catalog);
        var query = Query([]) with
        {
            SearchVector = new SemanticVector("embedding-v1", [1, 0]),
            MaxSeeds = 1,
            MaxDepth = 0
        };

        var projection = compiler.Compile(query);

        var node = Assert.Single(projection.Nodes);
        Assert.Equal("concept.workflow", node.NodeId);
        Assert.Equal(1, node.VectorScore);
        Assert.Equal(0, node.TextScore);
        Assert.Throws<ArgumentException>(() => compiler.Compile(query with
        {
            SearchVector = new SemanticVector("embedding-v1", [1, 0, 0])
        }));
        Assert.Throws<ArgumentException>(() => compiler.Compile(query with
        {
            SearchVector = new SemanticVector("foreign-model", [1, 0])
        }));
        var hiddenSpace = Assert.Throws<ArgumentException>(() => compiler.Compile(query with
        {
            SearchVector = new SemanticVector("secret-space", [1, 0])
        }));
        var absentSpace = Assert.Throws<ArgumentException>(() => compiler.Compile(query with
        {
            SearchVector = new SemanticVector("absent-space", [1, 0])
        }));
        Assert.Equal(hiddenSpace.Message, absentSpace.Message);
    }

    [Fact]
    public void Question_lens_limits_projected_facets_without_changing_authority()
    {
        var compiler = new SemanticProjectionCompiler(Catalog());
        var projection = compiler.Compile(Query(["concept.workflow"]) with
        {
            FacetKeys = [SemanticFacets.How]
        });

        var node = Assert.Single(projection.Nodes, item => item.NodeId == "concept.workflow");
        Assert.Equal([SemanticFacets.How], node.Facets.Keys);
        Assert.Equal("Use a bounded task graph", node.Facets[SemanticFacets.How].Text);
        Assert.DoesNotContain("host.workflow", JsonSerializer.Serialize(node), StringComparison.Ordinal);
        Assert.Equal("task.graph.build", Assert.Single(projection.Bindings).TargetId);
    }

    [Fact]
    public void Concept_adjustment_needs_host_decision_and_expected_revision()
    {
        var catalog = Catalog();
        var before = new SemanticProjectionCompiler(catalog).Compile(Query(["concept.workflow"]));
        var proposal = new ConceptAdjustmentProposal(
            "proposal.1",
            catalog.CatalogHash,
            "concept.workflow",
            1,
            "builder",
            new Dictionary<string, SemanticFacet?>
            {
                [SemanticFacets.How] = Facet("Use an adjusted task graph", "receipt.adjustment")
            },
            "New evidence changes the method.",
            [new EvidenceRef("receipt", "receipt.adjustment")]);

        Assert.Throws<UnauthorizedAccessException>(() =>
            catalog.ApplyAdjustment(proposal, new DecisionAuthority(approve: false)));
        Assert.Throws<InvalidOperationException>(() => catalog.ApplyAdjustment(
            proposal with
            {
                FacetChanges = new Dictionary<string, SemanticFacet?>
                {
                    [SemanticFacets.How] = Facet("Use a bounded task graph", "host.how")
                }
            },
            new DecisionAuthority(approve: true)));
        var result = catalog.ApplyAdjustment(proposal, new DecisionAuthority(approve: true));
        var after = new SemanticProjectionCompiler(result.Catalog).Compile(Query(["concept.workflow"]));

        Assert.Equal("Use a bounded task graph", before.Nodes[0].Facets[SemanticFacets.How].Text);
        Assert.Equal("Use an adjusted task graph", after.Nodes[0].Facets[SemanticFacets.How].Text);
        Assert.NotEqual(before.ProjectionHash, after.ProjectionHash);
        Assert.Equal(2, after.Nodes[0].Revision);
        Assert.Equal("host.policy", result.Receipt.IssuerId);
        Assert.Equal(catalog.CatalogHash, result.Receipt.PreviousCatalogHash);
        Assert.Equal(result.Catalog.CatalogHash, result.Receipt.NewCatalogHash);
        Assert.Throws<InvalidOperationException>(() =>
            result.Catalog.ApplyAdjustment(proposal, new DecisionAuthority(approve: true)));
    }

    [Fact]
    public void Concept_adjustment_applies_the_exact_payload_seen_by_host_authority()
    {
        var catalog = Catalog();
        var changes = new Dictionary<string, SemanticFacet?>
        {
            [SemanticFacets.How] = Facet("Reviewed method", "host.reviewed")
        };
        var evidence = new List<EvidenceRef> { new("host", "host.reviewed") };
        var proposal = new ConceptAdjustmentProposal(
            "proposal.freeze", catalog.CatalogHash, "concept.workflow", 1,
            "builder", changes, "Review the method.", evidence);
        var authority = new MutatingAuthority(() =>
        {
            changes[SemanticFacets.How] = Facet("Unreviewed method", "host.unreviewed");
            evidence[0] = new EvidenceRef("host", "host.unreviewed");
        });

        var result = catalog.ApplyAdjustment(proposal, authority);
        var projected = new SemanticProjectionCompiler(result.Catalog)
            .Compile(Query(["concept.workflow"]));

        Assert.Equal("Reviewed method", projected.Nodes[0].Facets[SemanticFacets.How].Text);
        Assert.Equal("host.reviewed", Assert.Single(result.Receipt.EvidenceRefs).RefId);
        Assert.Equal("Reviewed method", authority.SeenFacetText);
    }

    [Fact]
    public void Catalog_snapshots_mutable_inputs_and_rejects_invalid_graphs()
    {
        var facets = new Dictionary<string, SemanticFacet>
        {
            [SemanticFacets.What] = Facet("Original concept", "host.original")
        };
        var vector = new float[] { 1, 0 };
        var node = Node("one", "alpha", facets, vector: new SemanticVector("model", vector));
        var catalog = new SemanticProjectionCatalog(
            [node], [], [new DomainPerspective("alpha-view", "alpha")]);
        facets[SemanticFacets.What] = Facet("Altered concept", "host.altered");
        vector[0] = 0;

        var projected = new SemanticProjectionCompiler(catalog).Compile(new SemanticProjectionQuery(
            ["alpha"], "alpha-view", ["one"]));
        Assert.Equal("Original concept", projected.Nodes[0].Facets[SemanticFacets.What].Text);
        Assert.Throws<ArgumentException>(() => new SemanticProjectionCatalog(
            [node, node], [], [new DomainPerspective("alpha-view", "alpha")]));
        Assert.Throws<ArgumentException>(() => new SemanticProjectionCatalog(
            [node], [new SemanticRelation("broken", "one", "missing", "links", SemanticClaimStatus.Proposed, [])],
            [new DomainPerspective("alpha-view", "alpha")]));
        Assert.Throws<ArgumentException>(() => new SemanticProjectionCatalog(
            [node], [], [new DomainPerspective("alpha-view", "alpha", "alpha-view")]));
        Assert.Throws<ArgumentException>(() => new SemanticProjectionCatalog(
            [Node("unproven", "alpha", new Dictionary<string, SemanticFacet>
            {
                [SemanticFacets.What] = new("unsupported observation", SemanticClaimStatus.Observed, [])
            })], [], [new DomainPerspective("alpha-view", "alpha")]));
    }

    [Fact]
    public void Unknown_relation_remains_unknown_without_making_a_negative_claim()
    {
        var catalog = new SemanticProjectionCatalog(
            [Node("first", "alpha", new Dictionary<string, SemanticFacet>()),
             Node("second", "alpha", new Dictionary<string, SemanticFacet>())],
            [new SemanticRelation("relation.unknown", "first", "second", "may-depend-on",
                SemanticClaimStatus.Unknown, [])],
            [new DomainPerspective("alpha-view", "alpha")]);

        var projection = new SemanticProjectionCompiler(catalog).Compile(
            new SemanticProjectionQuery(["alpha"], "alpha-view", ["first"]));

        Assert.Equal(SemanticClaimStatus.Unknown, Assert.Single(projection.Relations).Status);
        Assert.Empty(projection.EvidenceRefs);
    }

    [Fact]
    public void Planning_frame_adapter_emits_bounded_scoped_projection()
    {
        var adapter = new SemanticPlanningFrameProjector(Catalog(), _ => Query(["concept.workflow"]));
        var request = new PlanningFrameProjectionRequest(
            "run.1", 1, new RunRequest("Plan a workflow"),
            PlanningExecutionContext.Empty, [], [], [], null);

        var frame = Assert.Single(adapter.Project(request));

        Assert.Equal("semantic-projection", frame.Kind);
        Assert.StartsWith("sha256-v1:", frame.FrameId, StringComparison.Ordinal);
        Assert.Contains(frame.EvidenceRefs, reference => reference.RefId == "host.workflow");
        Assert.DoesNotContain("beta.secret", JsonSerializer.Serialize(frame.Payload), StringComparison.Ordinal);
        var tiny = new SemanticPlanningFrameProjector(
            Catalog(), _ => Query(["concept.workflow"]), maxPayloadBytes: 1024);
        Assert.Throws<InvalidOperationException>(() => tiny.Project(request));
    }

    [Fact]
    public async Task Runner_passes_semantic_projection_through_its_planning_snapshot()
    {
        var planner = new CapturingPlanner();
        var runner = new AgenticaRunner(
            planner,
            ToolCatalog.Create(),
            new InMemoryEventSink(),
            new DeterministicOutcomeReporter(),
            new ExecutionPolicy(MaxBlockedRetries: 0),
            PlanExhaustionCompletionEvaluator.Instance,
            new SemanticPlanningFrameProjector(Catalog(), _ => Query(["concept.workflow"])));

        _ = await runner.RunAsync(new RunRequest("Inspect the workflow perspective"));

        var planningRequest = Assert.IsType<PlanningRequest>(planner.LastRequest);
        var frame = Assert.Single(planningRequest.ContextFrames,
            item => item.Kind == "semantic-projection");
        Assert.Contains("concept.workflow", JsonSerializer.Serialize(frame.Payload), StringComparison.Ordinal);
        Assert.DoesNotContain("beta.secret", JsonSerializer.Serialize(frame.Payload), StringComparison.Ordinal);
    }

    private static SemanticProjectionQuery Query(
        IReadOnlyList<string> seeds,
        int maxDepth = 2) =>
        new(["general", "alpha"], "builder", seeds, MaxDepth: maxDepth);

    private static SemanticProjectionCatalog Catalog(bool reverse = false, string hiddenText = "private value")
    {
        var workflow = Node(
            "concept.workflow", "alpha",
            new Dictionary<string, SemanticFacet>
            {
                [SemanticFacets.What] = Facet("A governed workflow", "host.workflow")
            },
            new Dictionary<string, IReadOnlyDictionary<string, SemanticFacet>>
            {
                ["general"] = new Dictionary<string, SemanticFacet>
                {
                    [SemanticFacets.Why] = Facet("Frame the objective", "host.why")
                },
                ["builder"] = new Dictionary<string, SemanticFacet>
                {
                    [SemanticFacets.How] = Facet("Use a bounded task graph", "host.how")
                }
            },
            new SemanticVector("embedding-v1", [1, 0]));
        var task = Node("concept.task", "alpha",
            new Dictionary<string, SemanticFacet>
            {
                [SemanticFacets.What] = Facet("An executable task", "host.task")
            }, vector: new SemanticVector("embedding-v1", [0, 1]));
        var hidden = Node("beta.secret", "beta",
            new Dictionary<string, SemanticFacet>
            {
                [SemanticFacets.What] = Facet(hiddenText, "host.private")
            }, vector: new SemanticVector("secret-space", [1, 0]));
        SemanticConceptNode[] nodes = [workflow, task, hidden];
        SemanticRelation[] relations =
        [
            new("relation.depends", "concept.workflow", "concept.task", "depends-on",
                SemanticClaimStatus.Observed, [new EvidenceRef("host", "host.workflow")]),
            new("relation.recurs", "concept.task", "concept.workflow", "informs",
                SemanticClaimStatus.Inferred, []),
            new("relation.hidden", "concept.workflow", "beta.secret", "references",
                SemanticClaimStatus.Observed, [new EvidenceRef("host", "host.private")])
        ];
        DomainPerspective[] perspectives =
        [
            new("general", "general"),
            new("builder", "alpha", "general"),
            new("beta-view", "beta", "general")
        ];
        if (reverse)
        {
            Array.Reverse(nodes);
            Array.Reverse(relations);
            Array.Reverse(perspectives);
        }

        SemanticBinding[] bindings =
        [
            new("binding.workflow", "concept.workflow", "task-graph", "task.graph.build", "alpha",
                "builder", SemanticClaimStatus.Observed,
                [new EvidenceRef("host", "host.binding")]),
            new("binding.cross-domain", "concept.workflow", "task-graph", "beta.private.task", "beta",
                "builder", SemanticClaimStatus.Observed,
                [new EvidenceRef("host", "host.private")]),
            new("binding.hidden", "beta.secret", "task-graph", "beta.private.task", "beta",
                "beta-view", SemanticClaimStatus.Observed,
                [new EvidenceRef("host", "host.private")])
        ];
        if (reverse)
        {
            Array.Reverse(bindings);
        }

        return new SemanticProjectionCatalog(nodes, relations, perspectives, bindings);
    }

    private static SemanticConceptNode Node(
        string id,
        string domain,
        IReadOnlyDictionary<string, SemanticFacet> baseFacets,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, SemanticFacet>>? overlays = null,
        SemanticVector? vector = null) =>
        new(id, domain, "concept", 1, baseFacets,
            overlays ?? new Dictionary<string, IReadOnlyDictionary<string, SemanticFacet>>(),
            [], vector);

    private static SemanticFacet Facet(string text, string refId) =>
        new(text, SemanticClaimStatus.Observed, [new EvidenceRef("host", refId)]);

    private sealed class DecisionAuthority(bool approve) : IConceptAdjustmentAuthority
    {
        public ConceptAdjustmentAuthorization? Authorize(
            ConceptAdjustmentProposal proposal,
            SemanticConceptNode currentNode) =>
            approve ? new("decision.1", "host.policy") : null;
    }

    private sealed class MutatingAuthority(Action mutateSource) : IConceptAdjustmentAuthority
    {
        public string? SeenFacetText { get; private set; }

        public ConceptAdjustmentAuthorization? Authorize(
            ConceptAdjustmentProposal proposal,
            SemanticConceptNode currentNode)
        {
            SeenFacetText = proposal.FacetChanges[SemanticFacets.How]?.Text;
            mutateSource();
            return new ConceptAdjustmentAuthorization("decision.freeze", "host.policy");
        }
    }

    private sealed class CapturingPlanner : IWorkflowPlanner
    {
        public PlanningRequest? LastRequest { get; private set; }

        public Task<WorkflowPlan> CreatePlanAsync(
            PlanningRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(new WorkflowPlan("plan.empty", 1, [], "Inspect only."));
        }

        public Task<WorkflowPlan> RefinePlanAsync(
            PlanningRequest request,
            Observation observation,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkflowPlan("plan.empty", 1, [], "Inspect only."));
    }
}
