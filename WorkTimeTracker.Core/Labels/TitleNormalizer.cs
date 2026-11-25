using System.Text.RegularExpressions;

namespace WorkTimeTracker.Core.Labels;

public sealed record TitleNormalizationRule(string Pattern, bool Enabled = true);

public static class TitleNormalizer
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    public static IReadOnlyList<TitleNormalizationRule> DefaultRules { get; } = Array.AsReadOnly(new[]
    {
        new TitleNormalizationRule(@"^\s*(?:(?:\(\d+\+?\)|\[\d+\+?\]|[•●*])\s*)+"),
        new TitleNormalizationRule(@"\s*(?:(?:\(\d+\+?\)|\[\d+\+?\]|[•●*])\s*)+(?=\s+-\s+|$)")
    });

    public static string Normalize(string title, IReadOnlyList<TitleNormalizationRule> rules)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(rules);
        foreach (var rule in rules)
            if (rule.Enabled)
                title = Regex.Replace(title, rule.Pattern, "", RegexOptions.CultureInvariant, RegexTimeout);
        return title.Trim();
    }

    public static void Validate(IEnumerable<TitleNormalizationRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        foreach (var rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Pattern))
                throw new ArgumentException("A title normalization rule needs a pattern.", nameof(rules));
            _ = new Regex(rule.Pattern, RegexOptions.CultureInvariant, RegexTimeout);
        }
    }
}
