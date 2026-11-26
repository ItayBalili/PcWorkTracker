using WorkTimeTracker.Core.Labels;
using WorkTimeTracker.Core.Models;

namespace WorkTimeTracker.Tests;

public sealed class TitleNormalizerTests
{
    [Theory]
    [InlineData("(4) Facebook - Google Chrome", "Facebook - Google Chrome")]
    [InlineData("(12+) Facebook - Google Chrome", "Facebook - Google Chrome")]
    [InlineData("[3] Facebook - Google Chrome", "Facebook - Google Chrome")]
    [InlineData("Inbox (12) - Gmail", "Inbox - Gmail")]
    [InlineData("Inbox (3) - Gmail", "Inbox - Gmail")]
    [InlineData("Inbox (12)", "Inbox")]
    [InlineData("*file.txt", "file.txt")]
    [InlineData("● file.txt", "file.txt")]
    [InlineData("file.txt •", "file.txt")]
    [InlineData("file.txt * - Notepad", "file.txt - Notepad")]
    public void NormalizesCountersAndMarkers(string title, string expected) =>
        Assert.Equal(expected, TitleNormalizer.Normalize(title, TitleNormalizer.DefaultRules));

    [Fact]
    public void DisabledRulePreservesRawTitleAndChangesAreRetroactive()
    {
        var original = "(4) Facebook - Google Chrome";
        var disabled = TitleNormalizer.DefaultRules.Select(rule => rule with { Enabled = false }).ToArray();
        Assert.Equal(original, TitleNormalizer.Normalize(original, disabled));
        Assert.Equal("Facebook - Google Chrome", TitleNormalizer.Normalize(original, TitleNormalizer.DefaultRules));
    }

    [Theory]
    [InlineData("chrome")]
    [InlineData("msedge")]
    [InlineData("firefox")]
    public void DefaultBrowserLabelsIgnoreLeadingCounters(string process)
    {
        var window = new WindowInfo("one", process, process, null, "(4) Facebook - Browser");
        Assert.Equal("Facebook - Browser", LabelExtractor.Extract(window, LabelExtractor.DefaultRules));
    }
}
