using WorkTimeTracker.Core.Models;

namespace WorkTimeTracker.Core.Abstractions;

public interface IInputMonitor
{
    IAsyncEnumerable<InteractionSecond> ReadInteractionsAsync(CancellationToken cancellationToken = default);
}
