using Dochub.Api.Domain;
using Dochub.Api.Endpoints;

namespace Dochub.Api.Services;

/// <summary>
/// Wraps a connector's stream so the provider's own 401/403 becomes a message a
/// person can act on, and the stored connection is marked expired — the same
/// treatment whether the import was started by hand or by a schedule.
/// </summary>
public static class AuthenticatedExtraction
{
    public static async IAsyncEnumerable<ExtractedDocument> Guarded(
        IAsyncEnumerable<ExtractedDocument> source,
        SourceConnection? connection,
        Func<CancellationToken, Task> onTokenRejected,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await using var enumerator = source.GetAsyncEnumerator(ct);
        while (true)
        {
            ExtractedDocument current;
            try
            {
                if (!await enumerator.MoveNextAsync()) yield break;
                current = enumerator.Current;
            }
            catch (HttpRequestException ex) when (
                ex.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                if (connection is not null)
                {
                    connection.Status = ConnectionStatus.Expired;
                    connection.UpdatedAt = DateTimeOffset.UtcNow;
                    await onTokenRejected(ct);
                }

                if (connection is null)
                    throw new InvalidOperationException(
                        $"The source refused access ({(int)ex.StatusCode!} {ex.StatusCode}). " +
                        "If it isn't public, connect an account that can read it.", ex);

                var label = connection.SourceType.Label();
                throw new InvalidOperationException(
                    $"{label} rejected the stored token ({(int)ex.StatusCode!} {ex.StatusCode}). " +
                    "Reconnect the source and try again.", ex);
            }
            yield return current;
        }
    }
}
