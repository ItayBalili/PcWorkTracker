using WorkTimeTracker.Core.Models;

namespace WorkTimeTracker.Core.Abstractions;

public interface ISessionMonitor
{
    IAsyncEnumerable<SessionEvent> ReadSessionEventsAsync(CancellationToken cancellationToken = default);
}
