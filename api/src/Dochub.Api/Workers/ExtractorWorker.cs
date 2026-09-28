using System.Text.Json;
using Dochub.Api.Services;
using Microsoft.Extensions.Options;

namespace Dochub.Api.Workers;

/// <summary>
/// The extractor service. Takes one source off the extract queue at a time and
/// moves its files into blob storage.
///
/// Sequential by design: a single large repository should slow the queue down,
/// not exhaust connections or blob throughput for everything behind it, and a
/// failure is always attributable to one source.
///
/// It shares only the database and the queue with the API, so setting
/// Extractor:Enabled=false here and true in a second deployment moves it out of
/// this process with no other change.
/// </summary>
public class ExtractorWorker(
    IQueueClient queues,
    IServiceScopeFactory scopes,
    IOptions<ServiceBusOptions> serviceBus,
    IOptions<ExtractorOptions> options,
    ILogger<ExtractorWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!options.Value.Enabled)
        {
            log.LogInformation("Extractor worker disabled (Extractor:Enabled=false).");
            return;
        }

        var queue = serviceBus.Value.ExtractQueue;
        var idleDelay = TimeSpan.FromSeconds(Math.Max(1, options.Value.IdlePollSeconds));
        log.LogInformation("Extractor worker listening on {Queue}, one message at a time", queue);

        while (!ct.IsCancellationRequested)
        {
            QueueLease? lease = null;
            try
            {
                lease = await queues.ReceiveAsync(queue, ct);
                if (lease is null)
                {
                    await Task.Delay(idleDelay, ct);
                    continue;
                }

                var request = DochubEvents.Deserialize<SourceUploadRequestedEvent>(lease.Payload);
                log.LogInformation("Extracting {Reference} (attempt {Attempt})", request.Reference, lease.DeliveryCount);

                using var scope = scopes.CreateScope();
                var extractor = scope.ServiceProvider.GetRequiredService<IExtractorService>();
                await extractor.HandleAsync(request, ct);

                await lease.CompleteAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Extract failed for a message on {Queue}", queue);
                if (lease is not null)
                {
                    try
                    {
                        // A body we cannot read will never parse, however many times
                        // we retry it — park it rather than spin.
                        if (ex is JsonException or InvalidOperationException { InnerException: JsonException })
                            await lease.DeadLetterAsync("Unreadable message body", ct);
                        else
                            await lease.AbandonAsync(ex.Message, ct);
                    }
                    catch (Exception releaseError)
                    {
                        // The lock lapses on its own, so the message is not lost.
                        log.LogError(releaseError, "Could not release the message lock; it will expire instead");
                    }
                }
                else
                {
                    await Task.Delay(idleDelay, ct);
                }
            }
        }
    }
}
