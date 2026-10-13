using System.Text.RegularExpressions;

namespace Exchange.Api.Tests.Repository;

/// <summary>
/// The source half of INV-14: money code holds no binary floating point and no truncating rounding
/// mode (spec 1.3, D-011). Pure functions over file text, so each rule can be shown to fire. Strings,
/// characters and comments are removed first: they may name what the code must not use.
/// </summary>
internal static partial class MoneySourceScan
{
    /// <summary>
    /// Whether a file deals in money: it mentions <c>decimal</c> or holds an <c>m</c>-suffixed literal.
    /// Wider than "declares a decimal", so no declaration form (<c>var x = 1m</c>, <c>decimal[]</c>,
    /// generic arguments) lets a file escape the scan.
    /// </summary>
    public static bool DealsInMoney(string source)
    {
        var code = CodeOnly(source);
        return DecimalKeywordPattern().IsMatch(code) || DecimalLiteralPattern().IsMatch(code);
    }

    /// <summary>The offending tokens in a file's code.</summary>
    public static IReadOnlyList<string> Offences(string source)
    {
        var code = CodeOnly(source);
        return [.. ForbiddenNamePattern().Matches(code).Concat(RealLiteralPattern().Matches(code)).Select(match => match.Value).Distinct()];
    }

    /// <summary>The code of a file without string literals, character literals and comments.</summary>
    public static string CodeOnly(string source) => StringsAndCommentsPattern().Replace(source, " ");

    // Raw strings, verbatim and interpolated strings, regular strings, char literals, block and line
    // comments.
    [GeneratedRegex("""\$*"{3,}[\s\S]*?"{3,}|@"(?:[^"]|"")*"|\$?"(?:[^"\\\n]|\\.)*"|'(?:[^'\\\n]|\\.)+'|/\*[\s\S]*?\*/|//[^\n]*""")]
    private static partial Regex StringsAndCommentsPattern();

    [GeneratedRegex(@"\bdecimal\b")]
    private static partial Regex DecimalKeywordPattern();

    [GeneratedRegex(@"\b[0-9][0-9_]*(?:\.[0-9]+)?[mM]\b")]
    private static partial Regex DecimalLiteralPattern();

    // Binary floating-point types and conversions, the truncating rounding mode in any spelling.
    [GeneratedRegex(@"\b(?:double|float|Double|Single|Half|MathF|ToDouble|ToSingle|ToHalf|ToZero)\b")]
    private static partial Regex ForbiddenNamePattern();

    // A real literal: a fraction, an exponent or an f/d suffix, without the decimal m suffix.
    [GeneratedRegex(@"(?<![\w.])[0-9][0-9_]*(?:\.[0-9][0-9_]*(?:[eE][+-]?[0-9]+)?(?![0-9_mM.])[fFdD]?|[eE][+-]?[0-9]+(?![0-9mM])[fFdD]?|[fFdD]\b)")]
    private static partial Regex RealLiteralPattern();
}
