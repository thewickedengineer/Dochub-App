using Dochub.Api.Services;

namespace Dochub.Api.Tests;

public class QueueBackoffTests
{
    [Fact]
    public void Backoff_grows_with_each_attempt()
    {
        var first = DatabaseQueueClient.BackoffFor(1);
        var second = DatabaseQueueClient.BackoffFor(2);
        var third = DatabaseQueueClient.BackoffFor(3);

        Assert.Equal(TimeSpan.FromSeconds(15), first);
        Assert.True(second > first);
        Assert.True(third > second);
    }

    [Fact]
    public void Backoff_is_capped_so_a_stuck_message_still_retries()
    {
        // Without a ceiling the delay would run to days; an hour keeps a message
        // that is waiting on a reconnected token moving again the same day.
        Assert.Equal(TimeSpan.FromHours(1), DatabaseQueueClient.BackoffFor(20));
    }

    [Fact]
    public void A_first_attempt_is_never_negative()
    {
        Assert.True(DatabaseQueueClient.BackoffFor(0) > TimeSpan.Zero);
    }
}
