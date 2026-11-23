using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Storage.Blobs;
using GlitchPilot.Core.Configuration;
using GlitchPilot.Core.Models;
using Microsoft.Extensions.Logging;

namespace GlitchPilot.Core.Services;

/// <summary>Persists triage runs: pending until the delayed report is sent, then the final usage JSON.</summary>
public interface IRunStore
{
    /// <summary>False when no storage is configured — the triage email is then sent immediately.</summary>
    bool IsEnabled { get; }

    Task SavePendingAsync(RunRecord run, CancellationToken ct = default);
    Task<List<RunRecord>> ListPendingAsync(CancellationToken ct = default);

    /// <summary>Writes the final record (without email bodies) and removes the pending one.</summary>
    Task CompleteAsync(RunRecord run, CancellationToken ct = default);
}

/// <summary>
/// Blob layout in <c>USAGE_CONTAINER</c>:
/// <c>pending/{runId}.json</c> while waiting for Copilot, <c>runs/yyyy/MM/dd/{runId}.json</c> once reported.
/// </summary>
public sealed class BlobRunStore : IRunStore
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly BlobContainerClient? _container;
    private readonly ILogger<BlobRunStore> _log;
    private bool _created;

    public BlobRunStore(ILogger<BlobRunStore> log)
    {
        _log = log;
        var connection = EnvironmentConfig.UsageStorageConnection;
        if (!string.IsNullOrWhiteSpace(connection))
            _container = new BlobContainerClient(connection, EnvironmentConfig.UsageContainer);
    }

    public bool IsEnabled => _container is not null;

    public async Task SavePendingAsync(RunRecord run, CancellationToken ct = default)
    {
        var container = await ContainerAsync(ct);
        await container.GetBlobClient(PendingName(run.RunId))
            .UploadAsync(BinaryData.FromObjectAsJson(run, Json), overwrite: true, ct);
    }

    public async Task<List<RunRecord>> ListPendingAsync(CancellationToken ct = default)
    {
        var container = await ContainerAsync(ct);
        var runs = new List<RunRecord>();
        await foreach (var item in container.GetBlobsAsync(prefix: "pending/", cancellationToken: ct))
        {
            try
            {
                var content = await container.GetBlobClient(item.Name).DownloadContentAsync(ct);
                if (content.Value.Content.ToObjectFromJson<RunRecord>(Json) is { } run)
                    runs.Add(run);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _log.LogError(ex, "Could not read pending run {Blob}", item.Name);
            }
        }
        return runs.OrderBy(r => r.StartedAt).ToList();
    }

    public async Task CompleteAsync(RunRecord run, CancellationToken ct = default)
    {
        var container = await ContainerAsync(ct);
        var final = JsonSerializer.Deserialize<RunRecord>(JsonSerializer.Serialize(run, Json), Json)!;
        final.Emails = null;

        var name = $"runs/{run.StartedAt.UtcDateTime:yyyy/MM/dd}/{run.RunId}.json";
        await container.GetBlobClient(name).UploadAsync(BinaryData.FromObjectAsJson(final, Json), overwrite: true, ct);
        await container.GetBlobClient(PendingName(run.RunId)).DeleteIfExistsAsync(cancellationToken: ct);
        _log.LogInformation("Usage report stored as {Container}/{Blob}", container.Name, name);
    }

    private static string PendingName(string runId) => $"pending/{runId}.json";

    private async Task<BlobContainerClient> ContainerAsync(CancellationToken ct)
    {
        var container = _container ?? throw new InvalidOperationException(
            "No usage storage configured (USAGE_STORAGE_CONNECTION / AzureWebJobsStorage).");
        if (!_created)
        {
            await container.CreateIfNotExistsAsync(cancellationToken: ct);
            _created = true;
        }
        return container;
    }
}
