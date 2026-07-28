using System.Collections;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentica.Artifacts;
using Agentica.Events;
using Agentica.Observations;
using Agentica.Outcomes;
using Agentica.Planning;
using Agentica.Requests;
using Agentica.Runs;
using Agentica.Tools;

namespace Agentica.Execution;

/// <summary>
/// Detaches planner- and observer-facing records from the authoritative run ledger.
/// Structured values use the same bounded JSON-like snapshot contract as tool results.
/// </summary>
internal static class ExecutionRecordSnapshot
{
    private const int MaxSnapshotDepth = 32;
    private const int MaxCollectionItems = 16_384;
    private const int MaxSnapshotNodes = 16_384;
    private const int MaxSnapshotBytes = 1024 * 1024;
    private const int MaxStringBytes = 256 * 1024;

    private static readonly JsonSerializerOptions RequestRestoreOptions = new()
    {
        MaxDepth = 32,
        Converters = { new JsonStringEnumConverter() }
    };

    public static WorkflowPlan Plan(WorkflowPlan source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(source.Steps);

        var budget = new SnapshotBudget();
        budget.Visit(depth: 0);

        return new WorkflowPlan(
            budget.Text(source.PlanId, "plan id"),
            source.Version,
            ReadOnly(
                source.Steps,
                step => Step(step, budget, depth: 2),
                budget,
                depth: 1,
                "plan steps"),
            budget.Text(source.Description, "plan description"))
        {
            PlanningReason = budget.OptionalText(source.PlanningReason, "planning reason")
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

        var budget = new SnapshotBudget();
        budget.Visit(depth: 0);
        return new OutcomeReport(
            budget.Text(source.ReportId, "report id"),
            budget.Text(source.Summary, "report summary"),
            ReadOnly(
                source.Claims,
                claim => ReportClaim(claim, budget, depth: 2),
                budget,
                depth: 1,
                "report claims"));
    }

    public static CompletionEvaluation Completion(CompletionEvaluation source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(source.Blockers);
        ArgumentNullException.ThrowIfNull(source.EvidenceRefs);

        var budget = new SnapshotBudget();
        budget.Visit(depth: 0);
        return new CompletionEvaluation(
            source.Decision,
            source.StopReason,
            Strings(source.Blockers, budget, depth: 1, "completion blockers"),
            Evidence(source.EvidenceRefs, budget, depth: 1));
    }

    public static AgenticaRun ReportingRun(AgenticaRun source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var snapshot = new AgenticaRun(
            source.RunId,
            ProofRequest(source.Request),
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
        snapshot.GrantConsumptions.AddRange(source.GrantConsumptions.Select(GrantConsumption));
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
        => Request(source, restoreSupportedTypes: true);

    /// <summary>
    /// Creates the canonical JSON-like request representation used in returned and
    /// observer-facing proof. Unlike <see cref="Request"/>, this never rehydrates a
    /// caller-defined mutable DTO merely to preserve planner compatibility.
    /// </summary>
    public static RunRequest ProofRequest(RunRequest source)
        => Request(source, restoreSupportedTypes: false);

    private static RunRequest Request(RunRequest source, bool restoreSupportedTypes)
    {
        ArgumentNullException.ThrowIfNull(source);

        var budget = new SnapshotBudget();
        budget.Visit(depth: 0);
        IReadOnlyDictionary<string, object?>? context = null;
        if (source.Context is not null)
        {
            var sourceTypes = restoreSupportedTypes
                ? RequestContextTypes(source.Context)
                : null;
            var canonical = ToolResultNormalizer.SnapshotStructuredData(source.Context);
            budget.Structured(canonical, depth: 1);
            context = restoreSupportedTypes
                ? RequestContext(canonical, sourceTypes!)
                : canonical;
        }

        return new RunRequest(
            budget.Text(source.Objective, "request objective"),
            source.Origin,
            context,
            budget.OptionalText(source.AuthorizationScopeId, "authorization scope id"));
    }

    public static RunRequest PlannerRequest(RunRequest source) => Request(source);

    private static PlanStep Step(
        PlanStep source,
        SnapshotBudget budget,
        int depth)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(source.Input);
        ArgumentNullException.ThrowIfNull(source.DependsOn);

        budget.Visit(depth);
        var input = ToolResultNormalizer.SnapshotStructuredData(source.Input);
        budget.Structured(input, depth + 1);

        return new PlanStep(
            budget.Text(source.StepId, "plan step id"),
            budget.Text(source.ToolId, "plan tool id"),
            source.Kind,
            source.Effect,
            input)
        {
            Reason = budget.OptionalText(source.Reason, "plan step reason"),
            Intent = source.Intent is null
                ? null
                : Intent(source.Intent, budget, depth + 1),
            DependsOn = Strings(
                source.DependsOn,
                budget,
                depth + 1,
                "plan dependency ids"),
            BatchId = budget.OptionalText(source.BatchId, "plan batch id")
        };
    }

    private static ExecutionIntent Intent(
        ExecutionIntent source,
        SnapshotBudget budget,
        int depth)
    {
        ArgumentNullException.ThrowIfNull(source);
        budget.Visit(depth);
        return new ExecutionIntent(
            budget.Text(source.Action, "execution intent action"),
            budget.Text(source.Rationale, "execution intent rationale"),
            budget.OptionalText(source.ExpectedOutcome, "execution intent expected outcome"));
    }

    private static ReportClaim ReportClaim(
        ReportClaim source,
        SnapshotBudget budget,
        int depth)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(source.Evidence);
        budget.Visit(depth);
        return new ReportClaim(
            budget.Text(source.Text, "report claim"),
            Evidence(source.Evidence, budget, depth + 1));
    }

    internal static PlanRefinement PlanRefinement(PlanRefinement source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new PlanRefinement(
            source.FromPlanId,
            source.ToPlanId,
            source.Reason,
            Evidence(source.Evidence));
    }

    internal static ExecutionBatch Batch(ExecutionBatch source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new ExecutionBatch(
            source.BatchId,
            ReadOnly(source.StepIds),
            source.StartedAt,
            source.CompletedAt);
    }

    internal static ToolGrantConsumption GrantConsumption(ToolGrantConsumption source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var budget = new SnapshotBudget();
        budget.Visit(depth: 0);
        return new ToolGrantConsumption(
            budget.Text(source.GrantId, "grant id"),
            budget.Text(source.AuthorizationScopeId, "grant authorization scope id"),
            budget.Text(source.RunId, "grant run id"),
            source.AttemptNumber,
            budget.Text(source.StepId, "grant step id"),
            budget.Text(source.ToolId, "grant tool id"),
            budget.Text(source.ManifestHash, "grant manifest hash"),
            budget.Text(source.InvocationInputDigest, "grant invocation digest"),
            budget.Text(source.Issuer, "grant issuer"),
            source.ExpiresAt,
            ReadOnly(
                source.AllowedOutboundBoundaries,
                boundary =>
                {
                    if (!Enum.IsDefined(boundary))
                    {
                        throw new InvalidOperationException("Grant evidence contains an undefined outbound boundary.");
                    }

                    return boundary;
                },
                budget,
                depth: 1,
                "grant outbound boundaries"),
            ReadOnly(
                source.AllowedExternalOutputs,
                output =>
                {
                    if (!Enum.IsDefined(output))
                    {
                        throw new InvalidOperationException("Grant evidence contains an undefined external-output class.");
                    }

                    return output;
                },
                budget,
                depth: 1,
                "grant external-output classifications"),
            source.ConsumedAt);
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
            ToolManifestCompiler.SnapshotDescriptors(source.ToolDescriptors),
            executionContext,
            Evidence(source.ObservationRefs),
            Evidence(source.ReceiptRefs),
            ToolSurfacePolicy(source.PolicySummary));
    }

    public static PlanningFrame PlanningFrame(PlanningFrame source)
        => PlanningFrame(source, new SnapshotBudget(), depth: 0);

    internal static IReadOnlyList<PlanningFrame> PlanningFrames(
        IEnumerable<PlanningFrame> source)
    {
        var budget = new SnapshotBudget();
        return ReadOnly(
            source,
            frame => PlanningFrame(frame, budget, depth: 1),
            budget,
            depth: 0,
            "planning frames");
    }

    private static PlanningFrame PlanningFrame(
        PlanningFrame source,
        SnapshotBudget budget,
        int depth)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(source.Payload);
        ArgumentNullException.ThrowIfNull(source.EvidenceRefs);
        budget.Visit(depth);
        var payload = ToolResultNormalizer.SnapshotStructuredData(source.Payload);
        budget.Structured(payload, depth + 1);
        return new PlanningFrame(
            budget.Text(source.FrameId, "planning frame id"),
            budget.Text(source.Kind, "planning frame kind"),
            budget.Text(source.Version, "planning frame version"),
            source.CreatedAt,
            payload,
            Evidence(source.EvidenceRefs, budget, depth + 1))
        {
            ToolSurfaceId = budget.OptionalText(source.ToolSurfaceId, "planning frame tool-surface id")
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

    private static IReadOnlyList<EvidenceRef> Evidence(
        IReadOnlyList<EvidenceRef> source,
        SnapshotBudget budget,
        int depth) =>
        ReadOnly(
            source,
            item =>
            {
                ArgumentNullException.ThrowIfNull(item);
                budget.Visit(depth + 1);
                return new EvidenceRef(
                    budget.Text(item.Kind, "evidence kind"),
                    budget.Text(item.RefId, "evidence reference id"));
            },
            budget,
            depth,
            "evidence references");

    private static IReadOnlyDictionary<string, object?> ToolSurfacePolicy(
        IReadOnlyDictionary<string, object?> source)
        => ToolResultNormalizer.SnapshotStructuredData(source);

    private static IReadOnlyDictionary<string, object?> RequestContext(
        IReadOnlyDictionary<string, object?> canonical,
        IReadOnlyDictionary<string, Type?> sourceTypes)
    {
        var snapshot = new Dictionary<string, object?>(canonical.Count, StringComparer.Ordinal);
        foreach (var pair in canonical)
        {
            sourceTypes.TryGetValue(pair.Key, out var sourceType);
            snapshot.Add(pair.Key, RestoreSupportedRequestValue(pair.Value, sourceType));
        }

        return new ReadOnlyDictionary<string, object?>(snapshot);
    }

    private static IReadOnlyDictionary<string, Type?> RequestContextTypes(
        IReadOnlyDictionary<string, object?> source)
    {
        var sourceTypes = new Dictionary<string, Type?>(StringComparer.Ordinal);
        foreach (var pair in source)
        {
            if (sourceTypes.Count >= MaxCollectionItems)
            {
                throw new InvalidOperationException(
                    $"Request context exceeds the maximum of {MaxCollectionItems} entries.");
            }

            if (pair.Key is null)
            {
                throw new InvalidOperationException("Request context contains a null key.");
            }

            sourceTypes.Add(pair.Key, pair.Value?.GetType());
        }

        return new ReadOnlyDictionary<string, Type?>(sourceTypes);
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
        catch (Exception exception) when (
            (exception is JsonException or NotSupportedException) &&
            RuntimeExceptionBoundary.IsRecoverable(exception))
        {
            // The bounded canonical value remains safe and usable when a host-specific
            // concrete type cannot be reconstructed.
            return canonical;
        }
    }

    internal static IReadOnlyList<T> ReadOnly<T>(IEnumerable<T> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var snapshot = new List<T>();
        foreach (var item in source)
        {
            if (snapshot.Count >= MaxCollectionItems)
            {
                throw new InvalidOperationException(
                    $"Execution record exceeds the maximum of {MaxCollectionItems} collection items.");
            }

            snapshot.Add(item);
        }

        return new ReadOnlyCollection<T>(snapshot);
    }

    private static IReadOnlyList<TResult> ReadOnly<TSource, TResult>(
        IEnumerable<TSource> source,
        Func<TSource, TResult> snapshotItem,
        SnapshotBudget budget,
        int depth,
        string description)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(snapshotItem);
        budget.Visit(depth);
        var snapshot = new List<TResult>();
        foreach (var item in source)
        {
            if (snapshot.Count >= MaxCollectionItems)
            {
                throw new InvalidOperationException(
                    $"{description} exceeds the maximum of {MaxCollectionItems} items.");
            }

            snapshot.Add(snapshotItem(item));
        }

        return new ReadOnlyCollection<TResult>(snapshot);
    }

    private static IReadOnlyList<string> Strings(
        IEnumerable<string> source,
        SnapshotBudget budget,
        int depth,
        string description) =>
        ReadOnly(
            source,
            item =>
            {
                budget.Visit(depth + 1);
                return budget.Text(item, description);
            },
            budget,
            depth,
            description);

    private sealed class SnapshotBudget
    {
        private int _remainingNodes = MaxSnapshotNodes;
        private int _remainingBytes = MaxSnapshotBytes;

        public void Visit(int depth)
        {
            if (depth > MaxSnapshotDepth)
            {
                throw new InvalidOperationException(
                    $"Execution record exceeds the maximum depth of {MaxSnapshotDepth}.");
            }

            if (_remainingNodes <= 0)
            {
                throw new InvalidOperationException(
                    $"Execution record exceeds the global maximum of {MaxSnapshotNodes} nodes.");
            }

            _remainingNodes--;
        }

        public string Text(string value, string description)
        {
            ArgumentNullException.ThrowIfNull(value);
            var bytes = Encoding.UTF8.GetByteCount(value);
            if (bytes > MaxStringBytes)
            {
                throw new InvalidOperationException(
                    $"Execution-record {description} exceeds the maximum of {MaxStringBytes} UTF-8 bytes.");
            }

            Consume(bytes, description);
            return value;
        }

        public string? OptionalText(string? value, string description) =>
            value is null ? null : Text(value, description);

        public void Structured(object? value, int depth)
        {
            Visit(depth);
            switch (value)
            {
                case null:
                    Consume(4, "null value");
                    return;
                case string text:
                    Text(text, "structured string");
                    return;
                case IReadOnlyDictionary<string, object?> dictionary:
                    var dictionaryItems = 0;
                    foreach (var pair in dictionary)
                    {
                        if (dictionaryItems >= MaxCollectionItems)
                        {
                            throw new InvalidOperationException(
                                $"Structured execution record exceeds the maximum of {MaxCollectionItems} entries.");
                        }

                        Text(pair.Key, "structured-data key");
                        Structured(pair.Value, depth + 1);
                        dictionaryItems++;
                    }

                    return;
                case IEnumerable sequence:
                    var sequenceItems = 0;
                    foreach (var item in sequence)
                    {
                        if (sequenceItems >= MaxCollectionItems)
                        {
                            throw new InvalidOperationException(
                                $"Structured execution record exceeds the maximum of {MaxCollectionItems} items.");
                        }

                        Structured(item, depth + 1);
                        sequenceItems++;
                    }

                    return;
                case bool boolean:
                    Consume(boolean ? 4 : 5, "Boolean value");
                    return;
                case byte or sbyte or short or ushort or int or uint or long or ulong:
                    Text(Convert.ToString(value, CultureInfo.InvariantCulture)!, "integer value");
                    return;
                case float single when float.IsFinite(single):
                    Text(single.ToString("R", CultureInfo.InvariantCulture), "number value");
                    return;
                case double number when double.IsFinite(number):
                    Text(number.ToString("R", CultureInfo.InvariantCulture), "number value");
                    return;
                case decimal number:
                    Text(number.ToString(CultureInfo.InvariantCulture), "number value");
                    return;
                case JsonElement { ValueKind: JsonValueKind.Number } element:
                    Text(element.GetRawText(), "JSON number value");
                    return;
                default:
                    throw new InvalidOperationException(
                        $"Canonical execution record contains unsupported value type '{value.GetType().FullName}'.");
            }
        }

        private void Consume(int bytes, string description)
        {
            if (bytes < 0 || bytes > _remainingBytes)
            {
                throw new InvalidOperationException(
                    $"Execution-record {description} exceeds the global maximum of {MaxSnapshotBytes} bytes.");
            }

            _remainingBytes -= bytes;
        }
    }
}
