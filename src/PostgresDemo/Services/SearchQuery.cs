namespace PostgresDemo.Services;

/// <summary>
/// Turns free-form user input into a safe Postgres full-text query.
/// Pure logic, no database — fully unit-testable.
///
/// <para>
/// Why not pass the raw input to <c>to_tsquery</c>? Because <c>to_tsquery</c>
/// parses its argument as a query language: <c>&amp; | ! : * ( )</c> are
/// operators, so user input like <c>"C++: the (good) parts"</c> throws a
/// syntax error — or worse, means something the user never typed.
/// This helper strips operators, keeps words, and builds a prefix query
/// (<c>wireless:* &amp; headphones:*</c>) so partial words still match.
/// </para>
/// </summary>
public static class SearchQuery
{
    /// <summary>
    /// Builds a <c>tsquery</c> string for prefix matching on every word, e.g.
    /// <c>"wireless headphones"</c> becomes <c>"wireless:* &amp; headphones:*"</c>.
    /// Hyphenated words split into two terms (<c>"noise-cancelling"</c> becomes
    /// <c>"noise:* &amp; cancelling:*"</c>) because a hyphen is not a valid
    /// tsquery lexeme character. Returns an empty string when the input has
    /// no searchable words.
    /// </summary>
    public static string ToTsQuery(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var terms = input
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(t => t.Split('-', StringSplitOptions.RemoveEmptyEntries))
            .Select(SanitizeTerm)
            .Where(t => t.Length > 0)
            .Select(t => $"{t}:*");

        return string.Join(" & ", terms);
    }

    /// <summary>Keeps letters, digits and hyphens; drops tsquery operators and punctuation.</summary>
    public static string SanitizeTerm(string term)
    {
        var kept = term.Where(c => char.IsLetterOrDigit(c) || c == '-');
        return new string(kept.ToArray()).ToLowerInvariant();
    }

    /// <summary>Normalizes a tag for the array filter: lowercase, trimmed, no spaces.</summary>
    public static string NormalizeTag(string tag) =>
        SanitizeTerm(tag.Trim().Replace(' ', '-'));
}
