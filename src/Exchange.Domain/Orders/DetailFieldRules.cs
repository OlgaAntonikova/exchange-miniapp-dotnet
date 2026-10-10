using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Exchange.Domain.Orders;

/// <summary>
/// The detail field rules of spec 1.1, each a named constant: the client-typed IBAN, recipient
/// name, payment purpose and wallet address are normalized, then accepted or refused. A refusal
/// carries no value: the caller names the field, never what was typed (RULE-SECRETS).
/// </summary>
public static partial class DetailFieldRules
{
    /// <summary>The shortest IBAN, in characters.</summary>
    public const int IbanMinChars = 15;

    /// <summary>The longest IBAN, in characters.</summary>
    public const int IbanMaxChars = 34;

    /// <summary>The longest recipient name or payment purpose, in characters.</summary>
    public const int TextMaxChars = 140;

    /// <summary>The shortest wallet address, in characters.</summary>
    public const int WalletMinChars = 20;

    /// <summary>The longest wallet address, in characters.</summary>
    public const int WalletMaxChars = 128;

    private const int IbanCheckModulus = 97;
    private const int IbanCheckRemainder = 1;
    private const int IbanPrefixChars = 4; // country letters and check digits, moved to the end for the check
    private const int LetterValueOffset = 10; // ISO 13616: A=10 ... Z=35
    private const int DecimalBase = 10;
    private const int MaskVisibleChars = 4;
    private const string MaskEllipsis = "...";

    /// <summary>The IBAN without whitespace, upper-cased, if it is one.</summary>
    public static bool TryIban(string? text, out string iban)
    {
        iban = string.Concat((text ?? string.Empty).Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant();
        return iban.Length is >= IbanMinChars and <= IbanMaxChars
            && IbanShape().IsMatch(iban)
            && Mod97(iban[IbanPrefixChars..] + iban[..IbanPrefixChars]) == IbanCheckRemainder;
    }

    /// <summary>The recipient name with whitespace runs collapsed, if it is acceptable.</summary>
    public static bool TryRecipientName(string? text, out string name) => TryText(text, out name);

    /// <summary>The payment purpose with whitespace runs collapsed, if it is acceptable.</summary>
    public static bool TryPaymentPurpose(string? text, out string purpose) => TryText(text, out purpose);

    /// <summary>The trimmed wallet address, if it is acceptable (deliberately loose, NR-6).</summary>
    public static bool TryWalletAddress(string? text, out string address)
    {
        address = (text ?? string.Empty).Trim();
        return address.Length is >= WalletMinChars and <= WalletMaxChars && WalletShape().IsMatch(address);
    }

    /// <summary>The first and last four characters around <c>...</c>, or <c>...</c> alone for eight or fewer (spec 1.5).</summary>
    public static string? Mask(string? value) =>
        value is null ? null
        : value.Length <= 2 * MaskVisibleChars ? MaskEllipsis
        : value[..MaskVisibleChars] + MaskEllipsis + value[^MaskVisibleChars..];

    // ISO 7064 MOD 97-10 over the digits, each letter replaced by its two-digit value, one digit
    // at a time so no number grows beyond an int.
    private static int Mod97(string rearranged)
    {
        var remainder = 0;
        foreach (var c in rearranged)
        {
            var value = char.IsAsciiDigit(c) ? c - '0' : c - 'A' + LetterValueOffset;
            remainder = value >= DecimalBase
                ? ((remainder * DecimalBase * DecimalBase) + value) % IbanCheckModulus
                : ((remainder * DecimalBase) + value) % IbanCheckModulus;
        }

        return remainder;
    }

    // Whitespace runs become one space; the text must then be non-empty, at most TextMaxChars
    // characters (code points, not UTF-16 units), with no control or format character and no lone
    // surrogate - a format character such as a right-to-left override would reorder what the
    // operator reads.
    private static bool TryText(string? text, out string collapsed)
    {
        collapsed = string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (collapsed.Length == 0)
        {
            return false;
        }

        var characters = 0;
        for (var i = 0; i < collapsed.Length; i++)
        {
            if (Rune.DecodeFromUtf16(collapsed.AsSpan(i), out var rune, out var units) != System.Buffers.OperationStatus.Done)
            {
                return false;
            }

            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format)
            {
                return false;
            }

            characters++;
            i += units - 1;
        }

        return characters <= TextMaxChars;
    }

    [GeneratedRegex(@"\A[A-Z]{2}[0-9]{2}[A-Z0-9]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex IbanShape();

    [GeneratedRegex(@"\A[A-Za-z0-9_+/=:-]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex WalletShape();
}
