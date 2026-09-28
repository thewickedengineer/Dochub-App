using System.Text.Json;
using Dochub.Api.Data;
using Dochub.Api.Domain;
using Dochub.Api.Hubs;
using Dochub.Api.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Dochub.Api.Workers;

/// <summary>
/// A local stand-in for the Python vector service, so the flow can be seen end to
/// end before that service exists. It consumes the process queue exactly as the
/// real one will, advances the source through Processing → Processed, and marks
/// each document Indexed — but computes no embeddings and stores no vectors.
///
/// Turn it off (VectorService:SimulateLocally=false) as soon as the Python
/// service is running, or the two will compete for the same messages.
/// </summary>
public class VectorServiceSimulator(
    IQueueClient queues,
    IServiceScopeFactory scopes,
    IHubContext<NotificationHub, INotificationClient> hub,
    IOptions<ServiceBusOptions> serviceBus,
    IOptions<VectorServiceOptions> options,
    ILogger<VectorServiceSimulator> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!options.Value.SimulateLocally)
        {
            log.LogInformation("Vector service simulation off — the Python service owns the process queue.");
            return;
        }

        var queue = serviceBus.Value.ProcessQueue;
        log.LogWarning("Simulating the vector service on {Queue}. No embeddings are produced.", queue);

        while (!ct.IsCancellationRequested)
        {
            QueueLease? lease = null;
            try
            {
                lease = await queues.ReceiveAsync(queue, ct);
                if (lease is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                    continue;
                }

                var request = DochubEvents.Deserialize<DocumentsProcessRequestedEvent>(lease.Payload);
                await ProcessAsync(request, ct);
                await lease.CompleteAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                log.LogError(ex, "Simulated processing failed");
                if (lease is not null)
                {
                    try { await lease.AbandonAsync(ex.Message, ct); }
                    catch (Exception releaseError) { log.LogError(releaseError, "Could not release the lock"); }
                }
                else await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
    }

    private async Task ProcessAsync(DocumentsProcessRequestedEvent request, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DochubDbContext>();
        var notifications = scope.ServiceProvider.GetRequiredService<INotificationService>();

        var source = await db.SourceDocuments
            .Include(x => x.Documents)
            .FirstOrDefaultAsync(x => x.Id == request.SourceDocumentId, ct);
        if (source is null) return;

        source.Status = SourceDocumentStatus.Processing;
        source.ProcessingStartedAt = DateTimeOffset.UtcNow;
        foreach (var document in source.Documents.Where(d => d.Status == DocumentStatus.Uploaded))
        {
            document.Status = DocumentStatus.Processing;
            document.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        await PushAsync(source, request.ArtifactId, ct);

        var perDocument = TimeSpan.FromSeconds(Math.Max(0, options.Value.SimulatedSecondsPerDocument));
        var done = 0;

        foreach (var document in source.Documents.Where(d => d.Status == DocumentStatus.Processing).ToList())
        {
            if (perDocument > TimeSpan.Zero) await Task.Delay(perDocument, ct);

            document.Status = DocumentStatus.Indexed;
            document.UpdatedAt = DateTimeOffset.UtcNow;
            source.ProcessedDocumentCount = ++done;
            await db.SaveChangesAsync(ct);
            await PushAsync(source, request.ArtifactId, ct);
        }

        source.Status = source.FailedDocumentCount > 0
            ? SourceDocumentStatus.PartiallyFailed
            : SourceDocumentStatus.Processed;
        source.ProcessedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await PushAsync(source, request.ArtifactId, ct);

        await notifications.RaiseAsync(request.OrganizationId, null, "processing.completed",
            "Processing complete",
            $"{source.Reference} · {done} document{(done == 1 ? "" : "s")} from {request.ArtifactName} are now searchable.",
            NotificationSeverity.Success, "sourceDocument", source.Id, ct);

        log.LogInformation("{Reference}: {Count} document(s) marked indexed", source.Reference, done);
    }

    private Task PushAsync(SourceDocument source, Guid artifactId, CancellationToken ct) =>
        hub.Clients.Group(NotificationHub.OrgGroup(source.OrganizationId))
            .SourceProgress(new SourceProgressPayload(
                source.Id, source.Reference, artifactId, source.Status.ToString(),
                source.TotalDocuments, source.UploadedDocumentCount, source.ProcessedDocumentCount));
}
