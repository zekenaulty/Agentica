using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentica.Artifacts;
using Agentica.Events;
using Agentica.Observations;
using Agentica.Outcomes;
using Agentica.Planning;
using Agentica.Requests;
using Agentica.Runs;

namespace Agentica.Execution;

/// <summary>
/// Detaches planner- and observer-facing records from the authoritative run ledger.
/// Structured values use the same bounded JSON-like snapshot contract as tool results.
/// </summary>
internal static class ExecutionRecordSnapshot
{
    private static readonly JsonSerializerOptions RequestRestoreOptions = new()
    {
        MaxDepth = 32,
        Converters = { new JsonStringEnumConverter() }
    };

    public static WorkflowPlan Plan(WorkflowPlan source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(source.Steps);

        return new WorkflowPlan(
            source.PlanId,
            source.Version,
            ReadOnly(source.Steps.Select(Step)),
            source.Description)
        {
            PlanningReason = source.PlanningReason
        };
    }

    public static Receipt Receipt(Receipt source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new Receipt(
            source.ReceiptId,
            source.StepId,
            source.ToolId,
            source.Status,
            source.Message,
            source.At,
            ToolResultNormalizer.SnapshotStructuredData(source.Data));
    }

    public static Observation Observation(Observation source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new Observation(
            source.ObservationId,
            source.StepId,
            source.Kind,
            source.Summary,
            ToolResultNormalizer.SnapshotStructuredData(source.Data),
            Evidence(source.Evidence));
    }

    public static Artifact Artifact(Artifact source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new Artifact(
            source.ArtifactId,
            source.Kind,
            ToolResultNormalizer.SnapshotStructuredData(source.Payload),
            Evidence(source.Evidence));
    }

    public static OutcomeReport Report(OutcomeReport source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(source.Claims);
        return new OutcomeReport(
            source.ReportId,
            source.Summary,
            ReadOnly(source.Claims.Select(claim =>
            {
                ArgumentNullException.ThrowIfNull(claim);
                return new ReportClaim(claim.Text, Evidence(claim.Evidence));
            })));
    }

    public static AgenticaRun ReportingRun(AgenticaRun source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var snapshot = new AgenticaRun(
            source.RunId,
            Request(source.Request),
            source.AttemptNumber,
            source.CreatedAt)
        {
            Status = source.Status,
            EventDeliveryFailure = source.EventDeliveryFailure is null
                ? null
                : source.EventDeliveryFailure with { }
        };

        snapshot.PlanVersions.AddRange(source.PlanVersions.Select(Plan));
        snapshot.PlanRefinements.AddRange(source.PlanRefinements.Select(PlanRefinement));
        snapshot.CompletedSteps.AddRange(source.CompletedSteps);
        snapshot.Observations.AddRange(source.Observations.Select(Observation));
        snapshot.Artifacts.AddRange(source.Artifacts.Select(Artifact));
        snapshot.Receipts.AddRange(source.Receipts.Select(Receipt));
        snapshot.Batches.AddRange(source.Batches.Select(Batch));
        foreach (var executionEvent in source.Events)
        {
            snapshot.AddEvent(ExecutionEventSnapshot.Clone(executionEvent));
        }

        snapshot.ToolSurfaces.AddRange(source.ToolSurfaces.Select(ToolSurface));
        snapshot.PlanningFrames.AddRange(source.PlanningFrames.Select(PlanningFrame));
        foreach (var pair in source.PlanToolSurfaceIds)
        {
            snapshot.PlanToolSurfaceIds[pair.Key] = pair.Value;
        }

        foreach (var pair in source.PlanToolManifestHashes)
        {
            snapshot.PlanToolManifestHashes[pair.Key] = pair.Value;
        }

        snapshot.ExposedBoundaries.UnionWith(source.ExposedBoundaries);
        return snapshot;
    }

    public static RunRequest Request(RunRequest source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new RunRequest(
            source.Objective,
            source.Origin,
            source.Context is null ? null : RequestContext(source.Context));
    }

    public static RunRequest PlannerRequest(RunRequest source) => Request(source);

    private static PlanStep Step(PlanStep source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(source.Input);
        ArgumentNullException.ThrowIfNull(source.DependsOn);

        return new PlanStep(
            source.StepId,
            source.ToolId,
            source.Kind,
            source.Effect,
            ToolResultNormalizer.SnapshotStructuredData(source.Input))
        {
            Reason = source.Reason,
            Intent = source.Intent is null ? null : source.Intent with { },
            DependsOn = ReadOnly(source.DependsOn),
            BatchId = source.BatchId
        };
    }

    private static PlanRefinement PlanRefinement(PlanRefinement source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new PlanRefinement(
            source.FromPlanId,
            source.ToPlanId,
            source.Reason,
            Evidence(source.Evidence));
    }

    private static ExecutionBatch Batch(ExecutionBatch source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new ExecutionBatch(
            source.BatchId,
            ReadOnly(source.StepIds),
            source.StartedAt,
            source.CompletedAt);
    }

    public static ToolSurfaceSnapshot ToolSurface(ToolSurfaceSnapshot source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var executionContext = new PlanningExecutionContext(
            ReadOnly(source.ExecutionContext.CompletedStepIds),
            ReadOnly(source.ExecutionContext.CompletedSteps.Select(step => step with { })),
            source.ExecutionContext.CurrentPlanId,
            source.ExecutionContext.PlanVersionCount);
        return new ToolSurfaceSnapshot(
            source.SurfaceId,
            source.ManifestHash,
            source.CreatedAt,
            ReadOnly(source.ToolDescriptors),
            executionContext,
            Evidence(source.ObservationRefs),
            Evidence(source.ReceiptRefs),
            ToolSurfacePolicy(source.PolicySummary));
    }

    public static PlanningFrame PlanningFrame(PlanningFrame source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new PlanningFrame(
            source.FrameId,
            source.Kind,
            source.Version,
            source.CreatedAt,
            ToolResultNormalizer.SnapshotStructuredData(source.Payload),
            Evidence(source.EvidenceRefs))
        {
            ToolSurfaceId = source.ToolSurfaceId
        };
    }

    private static IReadOnlyList<EvidenceRef> Evidence(IReadOnlyList<EvidenceRef> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return ReadOnly(source.Select(item =>
        {
            ArgumentNullException.ThrowIfNull(item);
            return item with { };
        }));
    }

    private static IReadOnlyDictionary<string, object?> ToolSurfacePolicy(
        IReadOnlyDictionary<string, object?> source)
    {
        var structuredSnapshot = ToolResultNormalizer.SnapshotStructuredData(source);
        var snapshot = new Dictionary<string, object?>(source.Count, StringComparer.Ordinal);
        foreach (var pair in source)
        {
            // Policy summaries historically expose their string sequences as arrays.
            // Preserve that public shape while still detaching each array instance.
            snapshot[pair.Key] = pair.Value is string[] strings
                ? strings.ToArray()
                : structuredSnapshot[pair.Key];
        }

        return new ReadOnlyDictionary<string, object?>(snapshot);
    }

    private static IReadOnlyDictionary<string, object?> RequestContext(
        IReadOnlyDictionary<string, object?> source)
    {
        var sourceTypes = source.ToDictionary(
            pair => pair.Key,
            pair => pair.Value?.GetType(),
            StringComparer.Ordinal);
        var canonical = ToolResultNormalizer.SnapshotStructuredData(source);
        var snapshot = new Dictionary<string, object?>(canonical.Count, StringComparer.Ordinal);
        foreach (var pair in canonical)
        {
            sourceTypes.TryGetValue(pair.Key, out var sourceType);
            snapshot.Add(pair.Key, RestoreSupportedRequestValue(pair.Value, sourceType));
        }

        return new ReadOnlyDictionary<string, object?>(snapshot);
    }

    private static object? RestoreSupportedRequestValue(object? canonical, Type? sourceType)
    {
        if (canonical is null || sourceType is null)
        {
            return canonical;
        }

        if (sourceType == typeof(JsonElement))
        {
            return JsonSerializer.SerializeToElement(canonical, RequestRestoreOptions);
        }

        if (sourceType == typeof(string) || sourceType.IsInstanceOfType(canonical))
        {
            return canonical;
        }

        // Dictionaries and sequences already have bounded, deeply detached JSON-like
        // representations. Retaining those avoids reintroducing mutable aliases and
        // preserves the CLR scalar values used by retry-context consumers.
        if (typeof(System.Collections.IDictionary).IsAssignableFrom(sourceType) ||
            typeof(System.Collections.IEnumerable).IsAssignableFrom(sourceType))
        {
            return canonical;
        }

        if (sourceType.IsAbstract || sourceType.IsInterface || sourceType.ContainsGenericParameters)
        {
            return canonical;
        }

        try
        {
            var json = JsonSerializer.SerializeToElement(canonical, RequestRestoreOptions);
            return JsonSerializer.Deserialize(json, sourceType, RequestRestoreOptions) ?? canonical;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            // The bounded canonical value remains safe and usable when a host-specific
            // concrete type cannot be reconstructed.
            return canonical;
        }
    }

    private static IReadOnlyList<T> ReadOnly<T>(IEnumerable<T> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new ReadOnlyCollection<T>(source.ToArray());
    }
}
