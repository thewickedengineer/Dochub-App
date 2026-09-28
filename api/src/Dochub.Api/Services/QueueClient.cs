using System.Text.Json;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Dochub.Api.Data;
using Dochub.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Dochub.Api.Services;

/// <summary>One message claimed from a queue, held until it is completed or abandoned.</summary>
public record QueueLease(
    string MessageId,
    string Subject,
    string Payload,
    int DeliveryCount,
    Func<CancellationToken, Task> CompleteAsync,
    Func<string, CancellationToken, Task> AbandonAsync,
    Func<string, CancellationToken, Task> DeadLetterAsync);

/// <summary>
/// Send and receive against a queue. Receives are deliberately one message at a
/// time: the extractor works through sources sequentially so a large repo cannot
/// starve the others, and so failures are attributable to a single source.
/// </summary>
public interface IQueueClient
{
    Task SendAsync(string queue, string messageId, string subject, string payload, string? sessionId, CancellationToken ct);
    Task<QueueLease?> ReceiveAsync(string queue, CancellationToken ct);
}

/// <summary>Talks to a real Azure Service Bus namespace.</summary>
public class ServiceBusQueueClient : IQueueClient, IAsyncDisposable
{
    private const int MaxDeliveryAttempts = 5;
    private readonly ServiceBusClient _client;
    private readonly Dictionary<string, ServiceBusSender> _senders = [];
    private readonly Dictionary<string, ServiceBusReceiver> _receivers = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<ServiceBusQueueClient> _log;

    public ServiceBusQueueClient(IOptions<ServiceBusOptions> options, ILogger<ServiceBusQueueClient> log)
    {
        _log = log;
        var o = options.Value;
        _client = !string.IsNullOrWhiteSpace(o.ConnectionString)
            ? new ServiceBusClient(o.ConnectionString)
            : new ServiceBusClient(o.Namespace, new DefaultAzureCredential());
    }

    public async Task SendAsync(string queue, string messageId, string subject, string payload, string? sessionId, CancellationToken ct)
    {
        var sender = await GetAsync(_senders, queue, q => _client.CreateSender(q));
        await sender.SendMessageAsync(new ServiceBusMessage(BinaryData.FromString(payload))
        {
            MessageId = messageId,   // broker-side dedupe when the outbox retries
            Subject = subject,
            ContentType = "application/json",
            SessionId = sessionId
        }, ct);
        _log.LogInformation("Sent {Subject} ({MessageId}) to {Queue}", subject, messageId, queue);
    }

    public async Task<QueueLease?> ReceiveAsync(string queue, CancellationToken ct)
    {
        var receiver = await GetAsync(_receivers, queue, q => _client.CreateReceiver(q, new ServiceBusReceiverOptions
        {
            ReceiveMode = ServiceBusReceiveMode.PeekLock
        }));

        var message = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(5), ct);
        if (message is null) return null;

        return new QueueLease(
            message.MessageId, message.Subject ?? "", message.Body.ToString(), message.DeliveryCount,
            token => receiver.CompleteMessageAsync(message, token),
            async (reason, token) =>
            {
                // Past the attempt ceiling the broker would loop forever, so park it.
                if (message.DeliveryCount >= MaxDeliveryAttempts)
                {
                    await receiver.DeadLetterMessageAsync(message, "MaxAttempts", reason, token);
                    return;
                }

                // Abandon alone redelivers immediately, which burns the attempt
                // budget in seconds. Re-send the body for later instead, then drop
                // the original so only the scheduled copy survives.
                var retryAt = DateTimeOffset.UtcNow.Add(DatabaseQueueClient.BackoffFor(message.DeliveryCount));
                var retry = new ServiceBusMessage(message.Body)
                {
                    MessageId = $"{message.MessageId}-retry-{message.DeliveryCount}",
                    Subject = message.Subject,
                    ContentType = message.ContentType,
                    SessionId = message.SessionId,
                    ScheduledEnqueueTime = retryAt
                };
                var sender = await GetAsync(_senders, queue, q => _client.CreateSender(q));
                await sender.SendMessageAsync(retry, token);
                await receiver.CompleteMessageAsync(message, token);
                _log.LogWarning("Message {MessageId} failed (attempt {Attempts}); re-queued for {RetryAt}: {Reason}",
                    message.MessageId, message.DeliveryCount, retryAt, reason);
            },
            (reason, token) => receiver.DeadLetterMessageAsync(message, "Rejected", reason, token));
    }

    private async Task<T> GetAsync<T>(Dictionary<string, T> cache, string queue, Func<string, T> create)
    {
        await _gate.WaitAsync();
        try
        {
            if (!cache.TryGetValue(queue, out var value)) cache[queue] = value = create(queue);
            return value;
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var s in _senders.Values) await s.DisposeAsync();
        foreach (var r in _receivers.Values) await r.DisposeAsync();
        await _client.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Stands in for Service Bus when no namespace is configured, so the local stack
/// keeps the same shape: durable messages, visibility timeouts, delivery counts
/// and one-at-a-time claims via SELECT ... FOR UPDATE SKIP LOCKED.
/// </summary>
public class DatabaseQueueClient(IServiceScopeFactory scopes, ILogger<DatabaseQueueClient> log) : IQueueClient
{
    private const int MaxDeliveryAttempts = 5;
    private static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(5);

    public async Task SendAsync(string queue, string messageId, string subject, string payload, string? sessionId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DochubDbContext>();

        if (await db.QueueMessages.AnyAsync(x => x.Queue == queue && x.MessageId == messageId, ct))
        {
            log.LogDebug("Message {MessageId} is already on {Queue}; ignoring the duplicate", messageId, queue);
            return;
        }

        db.QueueMessages.Add(new QueueMessage
        {
            Queue = queue, MessageId = messageId, Subject = subject,
            Payload = payload, SessionId = sessionId
        });
        await db.SaveChangesAsync(ct);
        log.LogInformation("Queued {Subject} ({MessageId}) on {Queue}", subject, messageId, queue);
    }

    public async Task<QueueLease?> ReceiveAsync(string queue, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DochubDbContext>();
        var now = DateTimeOffset.UtcNow;

        // The connection retries on transient faults, so the claim has to be one
        // retriable unit rather than a hand-rolled transaction.
        var claimed = await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            // SKIP LOCKED is what makes this safe to run in more than one process:
            // each consumer takes a different row instead of blocking on the same one.
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            var message = await db.QueueMessages
                .FromSql($"""
                    SELECT * FROM dochub.queue_messages
                    WHERE queue = {queue}
                      AND (status = 0 OR (status = 1 AND locked_until < {now}))
                    ORDER BY id
                    LIMIT 1
                    FOR UPDATE SKIP LOCKED
                    """)
                .FirstOrDefaultAsync(ct);

            if (message is null)
            {
                await transaction.RollbackAsync(ct);
                return null;
            }

            message.Status = QueueMessageStatus.InFlight;
            message.DeliveryCount++;
            message.LockedUntil = now.Add(LockDuration);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return message;
        });

        if (claimed is null) return null;

        var id = claimed.Id;
        var attempts = claimed.DeliveryCount;

        return new QueueLease(
            claimed.MessageId, claimed.Subject, claimed.Payload, attempts,
            token => UpdateAsync(id, m =>
            {
                m.Status = QueueMessageStatus.Completed;
                m.CompletedAt = DateTimeOffset.UtcNow;
                m.LockedUntil = null;
            }, token),
            (reason, token) => UpdateAsync(id, m =>
            {
                m.Error = Truncate(reason);
                if (attempts >= MaxDeliveryAttempts)
                {
                    m.Status = QueueMessageStatus.DeadLettered;
                    m.CompletedAt = DateTimeOffset.UtcNow;
                    m.LockedUntil = null;
                    log.LogError("Message {MessageId} dead-lettered after {Attempts} attempts: {Reason}",
                        m.MessageId, attempts, reason);
                }
                else
                {
                    // Held invisible for a backoff window rather than released
                    // immediately: an expired token or a rate limit needs time to
                    // change, and retrying instantly just burns the attempt budget.
                    var delay = BackoffFor(attempts);
                    m.Status = QueueMessageStatus.InFlight;
                    m.LockedUntil = DateTimeOffset.UtcNow.Add(delay);
                    log.LogWarning("Message {MessageId} failed (attempt {Attempts}); retrying in {Delay}: {Reason}",
                        m.MessageId, attempts, delay, reason);
                }
            }, token),
            (reason, token) => UpdateAsync(id, m =>
            {
                m.Status = QueueMessageStatus.DeadLettered;
                m.Error = Truncate(reason);
                m.CompletedAt = DateTimeOffset.UtcNow;
                m.LockedUntil = null;
            }, token));
    }

    private async Task UpdateAsync(long id, Action<QueueMessage> mutate, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DochubDbContext>();
        var message = await db.QueueMessages.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (message is null) return;
        mutate(message);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Exponential backoff, capped so a stuck message still retries hourly.</summary>
    public static TimeSpan BackoffFor(int attempts) =>
        TimeSpan.FromSeconds(Math.Min(3600, 15 * Math.Pow(4, Math.Max(0, attempts - 1))));

    private static string Truncate(string value) => value.Length <= 2000 ? value : value[..2000];
}
