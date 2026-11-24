using WorkTimeTracker.Core.Models;

namespace WorkTimeTracker.Core.Abstractions;

public interface IWindowSnapshotProvider
{
    ValueTask<IReadOnlyList<WindowSnapshot>> CaptureAsync(CancellationToken cancellationToken = default);
}
