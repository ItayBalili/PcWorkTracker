using WorkTimeTracker.Core.Labels;
using WorkTimeTracker.Core.Models;

namespace WorkTimeTracker.Tests;

public sealed class LabelExtractorTests
{
    [Theory]
    [InlineData("devenv", "Solution - Microsoft Visual Studio", "Solution")]
    [InlineData("Code", "file.cs - Folder - Visual Studio Code", "Folder")]
    [InlineData("WINWORD", "Plan - Word", "Plan")]
    [InlineData("WINWORD", "Plan [Read-Only] - Word - Compatibility Mode", "Plan")]
    [InlineData("EXCEL", "Budget - Excel", "Budget")]
    [InlineData("POWERPNT", "Deck - PowerPoint", "Deck")]
    [InlineData("chrome", "Full browser tab title", "Full browser tab title")]
    [InlineData("unknown", "Fallback title", "Fallback title")]
    public void Extracts_default_label_for_application(string process, string title, string expected)
    {
        var window = new WindowInfo("1", process, process, null, title);
        Assert.Equal(expected, LabelExtractor.Extract(window, LabelExtractor.DefaultRules));
    }

    [Fact]
    public void InvalidRegexFallsBackToTheRawTitleAndValidationRejectsMissingLabelGroup()
    {
        var window = new WindowInfo("1", "Code", "Visual Studio Code", null, "raw title");
        Assert.Equal(window.Title, LabelExtractor.Extract(window, [new LabelRule("Code", "(?<label>[" )]));
        Assert.Throws<ArgumentException>(() => LabelExtractor.Validate([new LabelRule("Code", "(.*)")]));
    }

    [Fact]
    public void Recalculates_label_from_same_stored_title_when_rule_changes()
    {
        var window = new WindowInfo("1", "Code", "Visual Studio Code", null, "main.cs - Alpha - Visual Studio Code");
        var first = new[] { new LabelRule("Code", @".+? - (?<label>Alpha) - Visual Studio Code") };
        var second = new[] { new LabelRule("Code", @".+? - (?<label>Beta) - Visual Studio Code") };
        Assert.Equal("Alpha", LabelExtractor.Extract(window, first));
        Assert.Equal(window.Title, LabelExtractor.Extract(window, second));
    }
}
