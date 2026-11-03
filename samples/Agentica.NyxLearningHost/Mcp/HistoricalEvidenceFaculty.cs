using Agentica.Mcp;
using Agentica.Projection;
using Agentica.Tools;
using System.Text.Json;

namespace Agentica.NyxLearningHost.Mcp;

/// <summary>One disposable, host-installed exact cut. The optional connector must return that
/// same source, not a semantically similar substitute. No environment, database or credentials.</summary>
public sealed class HistoricalEvidenceFaculty
{
    private readonly ObligationInputManifest _manifest;
    private readonly PresentedEvidenceSegment _segment;
    private readonly Func<CancellationToken, ValueTask<HistoricalSourceRead>> _read;
    private int _sourceReads;

    public HistoricalEvidenceFaculty(ObligationInputManifest manifest,
        Func<CancellationToken, ValueTask<HistoricalSourceRead>>? read = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.PresentedEvidence.Count != 1)
            throw new ArgumentException("This faculty revision installs exactly one source cut.", nameof(manifest));
        _manifest = manifest;
        _segment = manifest.PresentedEvidence[0];
        _read = read ?? (_ => ValueTask.FromResult(new HistoricalSourceRead(HistoricalSourceReadState.Available, _segment)));
        InstalledRequest = new(_segment.SegmentId, _segment.Source.RefId, _segment.SourceRevision,
            manifest.ScopeId, manifest.PerspectiveId, _segment.StartUtf16, _segment.Text.Length);
        Binding = McpTrustedBindingFactory.Create(HistoricalEvidenceContract.ServerId,
            HistoricalEvidenceContract.RemoteName, HistoricalEvidenceContract.InputSchemaSha256,
            HistoricalEvidenceContract.ToolId, "Read exact historical evidence",
            "Read one installed source cut. " + HistoricalEvidenceContract.Semantics.Applies,
            HistoricalEvidenceContract.InputSchemaJson, ToolKind.Query, ToolEffect.ReadOnly,
            [ToolDataBoundary.Public], [ToolDataBoundary.ExternalUntrusted],
            ToolExternalOutputClassification.Mixed, ToolApprovalRequirement.None,
            ToolRetrySafety.Idempotent, maxResultCharacters: 262_144);
        Binding = Binding with
        {
            Descriptor = Binding.Descriptor with
            {
                ContextHint = new(HistoricalEvidenceContract.OutputRevision, [], [], [])
                {
                    UseWhen = HistoricalEvidenceContract.Semantics.Requires,
                    NotEnoughWhen = "A read receipt alone does not establish consumer acceptance or obligation satisfaction."
                }
            }
        };
    }

    public McpToolBinding Binding { get; }
    public HistoricalEvidenceRequest InstalledRequest { get; }
    public int SourceReads => Volatile.Read(ref _sourceReads);

    public IMcpToolTransport InProcessTransport() => new LocalTransport(this);

    /// <summary>Retains the existing MCP readiness model and adds this host's pre-dispatch
    /// source/scope check. Remote discovery never changes the installed cut or binding.</summary>
    public async Task<McpBindingInspection> InspectAsync(IMcpToolTransport? transport,
        CancellationToken cancellationToken = default)
    {
        var inspection = await McpToolCatalogAdapter.InspectAsync(transport, [Binding],
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return inspection with
        {
            ReadyRegistrations = Array.AsReadOnly(inspection.ReadyRegistrations.Select(registration =>
                registration with { Tool = new ScopedTool(this, registration.Tool) }).ToArray())
        };
    }

    public async ValueTask<HistoricalEvidenceResult> ReadAsync(HistoricalEvidenceRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validate(request);
        Interlocked.Increment(ref _sourceReads);
        HistoricalSourceRead source;
        try { source = await _read(cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or HttpRequestException or TimeoutException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Result(ObligationResolutionState.Unavailable, "history.source_unavailable", request);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (source.State == HistoricalSourceReadState.Unavailable)
            return Result(ObligationResolutionState.Unavailable, "history.source_unavailable", request);
        if (source.State == HistoricalSourceReadState.Error)
            return Result(ObligationResolutionState.Indeterminate, "history.source_error", request);
        if (source.State != HistoricalSourceReadState.Available)
            throw new InvalidOperationException("Unknown historical source disposition.");
        if (source.Evidence != _segment)
            return Result(ObligationResolutionState.Indeterminate, "history.source_changed", request);
        return Result(ObligationResolutionState.Ready, "history.exact_cut", request, _segment);
    }

    /// <summary>Shared wire projection for the in-process adapter and low-level SDK server.</summary>
    public async Task<McpCallResult> CallAsync(string remoteName,
        IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        if (!string.Equals(remoteName, HistoricalEvidenceContract.RemoteName, StringComparison.Ordinal))
            throw new InvalidOperationException("Historical capability is not installed.");
        var result = await ReadAsync(Parse(arguments), cancellationToken).ConfigureAwait(false);
        return new(result.Resolution == ObligationResolutionState.Indeterminate,
            [result.ReasonCode], HistoricalEvidenceContract.Serialize(result));
    }

    private HistoricalEvidenceResult Result(ObligationResolutionState resolution, string reason,
        HistoricalEvidenceRequest request, PresentedEvidenceSegment? evidence = null) =>
        new(1, HistoricalEvidenceContract.OutputRevision, _manifest.ContentIdentity, resolution,
            reason, request, evidence, evidence is null
                ? _manifest.Coverage with { Status = ObligationCoverageStatus.Unknown }
                : _manifest.Coverage);

    private void Validate(HistoricalEvidenceRequest request)
    {
        if (request != InstalledRequest)
            throw new InvalidOperationException("Historical evidence request is outside the installed source cut.");
        // The manifest guards exact source bounds and Unicode at the presentation boundary.
        _ = _manifest.ReadCitation(new(request.SegmentId, request.StartUtf16, request.LengthUtf16));
    }

    private static HistoricalEvidenceRequest Parse(IReadOnlyDictionary<string, object?> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count != 7)
            throw new ArgumentException("Historical evidence input must match its installed schema.", nameof(arguments));
        return new(Text("segmentId"), Text("sourceId"), Text("sourceRevision"), Text("scopeId"),
            Text("perspectiveId"), Number("startUtf16"), Number("lengthUtf16"));

        string Text(string key)
        {
            if (!arguments.TryGetValue(key, out var value)) throw Invalid();
            var text = value switch
            {
                string item => item,
                JsonElement { ValueKind: JsonValueKind.String } item => item.GetString(),
                _ => null
            };
            return text is { Length: > 0 and <= 512 } ? text : throw Invalid();
        }
        int Number(string key)
        {
            if (!arguments.TryGetValue(key, out var value)) throw Invalid();
            return value switch
            {
                int item => item,
                long item when item is >= int.MinValue and <= int.MaxValue => (int)item,
                JsonElement { ValueKind: JsonValueKind.Number } item when item.TryGetInt32(out var number) => number,
                _ => throw Invalid()
            };
        }
        static ArgumentException Invalid() => new("Historical evidence input is malformed.", nameof(arguments));
    }

    private sealed class ScopedTool(HistoricalEvidenceFaculty owner, ITool inner) : ITool
    {
        public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(invocation.ToolId, HistoricalEvidenceContract.ToolId, StringComparison.Ordinal) ||
                !string.Equals(invocation.AuthorizationScopeId, owner._manifest.ScopeId, StringComparison.Ordinal))
                throw new InvalidOperationException("Historical invocation has no matching installed host scope.");
            owner.Validate(Parse(invocation.Input));
            return inner.ExecuteAsync(invocation, cancellationToken);
        }
    }

    private sealed class LocalTransport(HistoricalEvidenceFaculty owner) : IMcpToolTransport
    {
        public string ServerId => HistoricalEvidenceContract.ServerId;

        public Task<IReadOnlyList<McpDiscoveredTool>> ListToolsAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<McpDiscoveredTool>>(
                [new(HistoricalEvidenceContract.RemoteName, HistoricalEvidenceContract.InputSchemaJson)]);
        }

        public Task<McpCallResult> CallToolAsync(string remoteName,
            IReadOnlyDictionary<string, object?> input, CancellationToken cancellationToken) =>
            owner.CallAsync(remoteName, input, cancellationToken);
    }
}
