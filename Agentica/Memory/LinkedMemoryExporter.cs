namespace Agentica.Memory;

/// <summary>Host-owned mapping to a storage model; it does not change the core export's source identity.</summary>
public interface IMemoryExportMapper<TModel>
{
    TModel Map(MemoryIndexExport memoryExport);
}

/// <summary>Host-owned persistence. A failed or uncertain write must be reconciled by the host, not retried here.</summary>
public interface IMemoryStorageProvider<TModel>
{
    Task<MemoryStorageAcknowledgement> WriteAsync(string exportId, TModel model,
        CancellationToken cancellationToken = default);
}

public sealed record MemoryStorageAcknowledgement(string ExportId, string StorageReference);

public sealed class LinkedMemoryExporter<TModel>(IMemoryExportMapper<TModel> mapper, IMemoryStorageProvider<TModel> storage)
{
    private readonly IMemoryExportMapper<TModel> _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
    private readonly IMemoryStorageProvider<TModel> _storage = storage ?? throw new ArgumentNullException(nameof(storage));

    public async Task<MemoryStorageAcknowledgement> WriteAsync(MemoryIndexExport export,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(export);
        cancellationToken.ThrowIfCancellationRequested();
        var model = _mapper.Map(export);
        if (model is null) throw new InvalidOperationException("Memory export mapping returned no model.");
        cancellationToken.ThrowIfCancellationRequested();
        var acknowledgement = await _storage.WriteAsync(export.ExportId, model, cancellationToken).ConfigureAwait(false);
        if (acknowledgement is null || acknowledgement.ExportId != export.ExportId)
            throw new InvalidOperationException("Storage acknowledgement does not resolve to the exact memory export; reconcile the write before retrying.");
        MemoryGuard.Id(acknowledgement.StorageReference);
        return acknowledgement;
    }
}
