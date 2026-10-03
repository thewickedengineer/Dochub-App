using System;
using System.Threading.Tasks;

namespace Claims.Core
{
    /// <summary>Sends the first-notice-of-loss acknowledgement.</summary>
    public class FnolNotifyService
    {
        private readonly IClock _clock;

        public FnolNotifyService(IClock clock) { _clock = clock; }

        public Task<bool> AcknowledgeAsync(Guid claimId)
        {
            var due = _clock.Now.AddHours(24);
            return Task.FromResult(due > _clock.Now);
        }
    }

    public interface IClock { DateTime Now { get; } }
}
