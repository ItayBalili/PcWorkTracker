using System.Security.Cryptography;
using System.Text;
using WorkTimeTracker.Core.Models;

namespace WorkTimeTracker.Core.Calculation;

/// <summary>Reuses a stable prefix while recalculating the provisional idle tail.</summary>
public sealed class IncrementalCalculationEngine
{
    private CalculationEngine.CalculationCheckpoint? checkpoint;
    private byte[]? fingerprint;
    private TimeSpan lookahead;
    private byte[]? completedFingerprint;
    private DayReport? completedReport;

    public DateTimeOffset? LastRecalculatedFromUtc { get; private set; }

    public DayReport Calculate(WorkDayRawData data, TrackerSettings settings, DateTimeOffset asOfUtc)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        var end = (data.WorkDay.EndUtc ?? asOfUtc).ToUniversalTime();
        var start = data.WorkDay.StartUtc.ToUniversalTime();
        var activityLookahead = settings.ActiveTimeout > settings.AwayTimeout ? settings.ActiveTimeout : settings.AwayTimeout;
        var delay = activityLookahead;
        if (settings.SampleInterval > TimeSpan.MaxValue - delay - TimeSpan.FromSeconds(1))
            throw new ArgumentOutOfRangeException(nameof(settings), "The calculation lookahead is too large.");
        delay += settings.SampleInterval + TimeSpan.FromSeconds(1);
        byte[]? closedFingerprint = null;
        if (data.WorkDay.EndUtc is not null)
        {
            closedFingerprint = Fingerprint(data, settings, end, activityLookahead);
            if (completedFingerprint is not null && completedReport is not null &&
                CryptographicOperations.FixedTimeEquals(completedFingerprint, closedFingerprint))
            {
                LastRecalculatedFromUtc = end;
                return completedReport;
            }
        }
        else
        {
            completedFingerprint = null;
            completedReport = null;
        }
        if (checkpoint is not null &&
            (end < checkpoint.Cursor || lookahead != delay || fingerprint is null ||
             !CryptographicOperations.FixedTimeEquals(fingerprint, Fingerprint(data, settings, checkpoint.Cursor, activityLookahead))))
        {
            checkpoint = null;
            fingerprint = null;
        }

        LastRecalculatedFromUtc = checkpoint?.Cursor ?? start;
        DateTimeOffset? target = end >= start && end - start > delay ? end - delay : null;
        var result = CalculationEngine.Run(data, settings, asOfUtc, checkpoint, target);
        if (result.Checkpoint is { } next)
        {
            checkpoint = next;
            lookahead = delay;
            fingerprint = Fingerprint(data, settings, next.Cursor, activityLookahead);
        }
        if (closedFingerprint is not null)
        {
            completedFingerprint = closedFingerprint;
            completedReport = result.Report;
        }
        return result.Report;
    }

    private static byte[] Fingerprint(WorkDayRawData raw, TrackerSettings settings, DateTimeOffset cutoff, TimeSpan delay)
    {
        var horizon = cutoff > DateTimeOffset.MaxValue - delay ? DateTimeOffset.MaxValue : cutoff + delay;
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            static long Ticks(DateTimeOffset value) => value.UtcTicks;
            writer.Write(raw.WorkDay.Date.DayNumber);
            writer.Write(Ticks(raw.WorkDay.StartUtc));
            writer.Write(raw.WorkDay.EndUtc?.UtcTicks ?? -1);
            writer.Write((int)raw.WorkDay.EndReason);
            writer.Write(raw.TrackingStartedUtc?.UtcTicks ?? -1);
            writer.Write(settings.MinimumVisiblePercent);
            writer.Write(settings.InteractionWindow.Ticks);
            writer.Write(settings.ActiveTimeout.Ticks);
            writer.Write(settings.AwayTimeout.Ticks);
            foreach (var window in raw.Windows.OrderBy(x => x.Id, StringComparer.Ordinal))
            {
                writer.Write(window.Id); writer.Write(window.ProcessName); writer.Write(window.FriendlyName);
                writer.Write(window.ExecutablePath is not null); writer.Write(window.ExecutablePath ?? ""); writer.Write(window.Title);
            }
            writer.Write((byte)0xff);
            foreach (var segment in raw.VisibilitySegments.Where(x => x.StartUtc < cutoff)
                .OrderBy(x => x.WindowId, StringComparer.Ordinal).ThenBy(x => x.StartUtc))
            {
                writer.Write(segment.WindowId); writer.Write(Ticks(segment.StartUtc));
                writer.Write(Ticks(segment.EndUtc < cutoff ? segment.EndUtc : cutoff));
                writer.Write(segment.VisiblePercent); writer.Write(segment.IsForeground);
            }
            writer.Write((byte)0xfe);
            foreach (var input in raw.Interactions.Where(x => x.SecondUtc <= horizon)
                .OrderBy(x => x.SecondUtc).ThenBy(x => x.WindowId, StringComparer.Ordinal))
            {
                writer.Write(input.WindowId); writer.Write(Ticks(input.SecondUtc)); writer.Write((int)input.Kind);
            }
            writer.Write((byte)0xfd);
            foreach (var item in raw.SessionEvents.Where(x => x.TimeUtc <= horizon || x.Kind is SessionEventKind.Shutdown or SessionEventKind.Boot).OrderBy(x => x.TimeUtc).ThenBy(x => x.Kind))
            {
                writer.Write(Ticks(item.TimeUtc)); writer.Write((int)item.Kind);
            }
            writer.Write((byte)0xfc);
            foreach (var item in raw.TrackingEvents.Where(x => x.TimeUtc <= horizon).OrderBy(x => x.TimeUtc).ThenBy(x => x.Kind))
            {
                writer.Write(Ticks(item.TimeUtc)); writer.Write((int)item.Kind); writer.Write(item.EndUtc?.UtcTicks ?? -1);
            }
            writer.Write((byte)0xfb);
            foreach (var observation in raw.GlobalInputs.Where(x => x.ObservedUtc < cutoff).OrderBy(x => x.ObservedUtc))
            {
                writer.Write(Ticks(observation.ObservedUtc)); writer.Write(Ticks(observation.LastInputUtc));
            }
            writer.Write((byte)0xfa);
            foreach (var group in raw.GlobalInputs.Where(x => x.LastInputUtc <= horizon && x.ObservedUtc >= cutoff)
                .GroupBy(x => x.LastInputUtc).OrderBy(x => x.Key))
            {
                writer.Write(Ticks(group.Key)); writer.Write(Ticks(group.Min(x => x.ObservedUtc)));
            }
        }
        return SHA256.HashData(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)));
    }
}
