using System.Text.RegularExpressions;
using WorkTimeTracker.Core.Models;

namespace WorkTimeTracker.Core.Labels;

public sealed record LabelRule(string Application, string Pattern, bool Enabled = true);

public static class LabelExtractor
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    public static IReadOnlyList<LabelRule> DefaultRules { get; } = Array.AsReadOnly(new[]
    {
        new LabelRule("devenv", @"^(?<label>.+?) - Microsoft Visual Studio"),
        new LabelRule("Code", @"^(?:● )?(?:.+? - )?(?<label>[^-]+?) - Visual Studio Code"),
        new LabelRule("WINWORD", @"^(?<label>.+?)(?:\s+\[(?:Read-Only|Read Only)\])? - Word(?: - .*)?$"),
        new LabelRule("EXCEL", @"^(?<label>.+?)(?:\s+\[(?:Read-Only|Read Only)\])? - Excel(?: - .*)?$"),
        new LabelRule("POWERPNT", @"^(?<label>.+?)(?:\s+\[(?:Read-Only|Read Only)\])? - PowerPoint(?: - .*)?$"),
        new LabelRule("chrome", @"^(?<label>.+)$"),
        new LabelRule("msedge", @"^(?<label>.+)$"),
        new LabelRule("firefox", @"^(?<label>.+)$"),
        new LabelRule("brave", @"^(?<label>.+)$"),
        new LabelRule("opera", @"^(?<label>.+)$")
    });

    public static string Extract(WindowInfo window, IReadOnlyList<LabelRule> rules)
        => TryExtract(window, rules, out var label) ? label : window.Title ?? string.Empty;

    public static bool TryExtract(WindowInfo window, IReadOnlyList<LabelRule> rules, out string label)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(rules);
        var title = window.Title ?? string.Empty;
        foreach (var rule in rules)
        {
            if (!rule.Enabled || !MatchesApplication(rule.Application, window)) continue;
            try
            {
                var match = Regex.Match(title, rule.Pattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, RegexTimeout);
                if (match.Success && match.Groups["label"].Success)
                {
                    label = match.Groups["label"].Value.Trim();
                    if (label.Length > 0) return true;
                }
            }
            catch (ArgumentException) { }
            catch (RegexMatchTimeoutException) { }
        }
        label = string.Empty;
        return false;
    }

    public static void Validate(IEnumerable<LabelRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        foreach (var rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Application) || rule.Application != rule.Application.Trim())
                throw new ArgumentException("A label rule needs a trimmed application name.", nameof(rules));
            try
            {
                var regex = new Regex(rule.Pattern, RegexOptions.CultureInvariant, RegexTimeout);
                if (!regex.GetGroupNames().Contains("label", StringComparer.Ordinal))
                    throw new ArgumentException("A label rule must contain a named 'label' group.", nameof(rules));
            }
            catch (ArgumentException)
            {
                throw;
            }
        }
    }

    private static bool MatchesApplication(string application, WindowInfo window)
    {
        static string Normalize(string value) => Path.GetFileNameWithoutExtension(value);
        return string.Equals(Normalize(application), Normalize(window.ProcessName), StringComparison.OrdinalIgnoreCase) ||
               string.Equals(application, window.FriendlyName, StringComparison.OrdinalIgnoreCase);
    }
}
