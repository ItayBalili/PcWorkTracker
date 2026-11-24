namespace WorkTimeTracker.Core.Abstractions;

public interface IAutostart
{
    ValueTask<bool> IsEnabledAsync(CancellationToken cancellationToken = default);

    ValueTask SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default);
}
