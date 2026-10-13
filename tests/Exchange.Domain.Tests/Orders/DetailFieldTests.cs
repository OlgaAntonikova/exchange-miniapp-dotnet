using System.Globalization;
using System.Numerics;
using Exchange.Domain.Orders;
using Shouldly;

namespace Exchange.Domain.Tests.Orders;

/// <summary>
/// INV-6, the detail field rules of spec 1.1, each with negative rows (RULE-NEGATIVE-TEST). IBANs at
/// the length bounds get their check digits from an independent ISO 7064 computation below.
/// </summary>
public sealed class DetailFieldTests
{
    [Fact]
    public void The_bounds_are_the_numbers_of_spec_1_1()
    {
        // Written out, not taken from the constants, so a changed constant fails here.
        DetailFieldRules.IbanMinChars.ShouldBe(15);
        DetailFieldRules.IbanMaxChars.ShouldBe(34);
        DetailFieldRules.TextMaxChars.ShouldBe(140);
        DetailFieldRules.WalletMinChars.ShouldBe(20);
        DetailFieldRules.WalletMaxChars.ShouldBe(128);
    }

    // --- IBAN ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData("ME25505000012345678951", "ME25505000012345678951")] // the published Montenegro example
    [InlineData("ME25 5050 0001 2345 6789 51", "ME25505000012345678951")] // grouped as printed
    [InlineData("GB82 WEST 1234 5698 7654 32", "GB82WEST12345698765432")]
    [InlineData("de89 3704 0044 0532 0130 00", "DE89370400440532013000")] // lower case
    [InlineData(" DE89370400440532013000\t", "DE89370400440532013000")]
    public void A_valid_iban_is_normalized(string text, string expected)
    {
        DetailFieldRules.TryIban(text, out var iban).ShouldBeTrue();
        iban.ShouldBe(expected);
    }

    [Fact]
    public void An_iban_with_the_letter_a_is_accepted()
    {
        // A is worth exactly 10, the smallest two-digit letter value.
        var iban = WithCheckDigits("AD", "AAAA12345698765432");

        DetailFieldRules.TryIban(iban, out var normalized).ShouldBeTrue();
        normalized.ShouldBe(iban);
    }

    [Fact]
    public void A_bad_shape_is_refused_even_when_the_check_digits_hold()
    {
        DetailFieldRules.TryIban(WithCheckDigits("12", "WEST12345698765432"), out _).ShouldBeFalse(); // the country is digits
        DetailFieldRules.TryIban(WithLetterCheckDigits("GB", "WEST1234569876543"), out _).ShouldBeFalse(); // the check digits are letters
    }

    [Fact]
    public void Ibans_at_both_length_bounds_are_accepted()
    {
        DetailFieldRules.TryIban(WithCheckDigits("NO", new string('1', DetailFieldRules.IbanMinChars - 4)), out _).ShouldBeTrue();
        DetailFieldRules.TryIban(WithCheckDigits("LC", new string('1', DetailFieldRules.IbanMaxChars - 4)), out _).ShouldBeTrue();
    }

    [Fact]
    public void Ibans_just_outside_the_length_bounds_are_refused()
    {
        DetailFieldRules.TryIban(WithCheckDigits("NO", new string('1', DetailFieldRules.IbanMinChars - 5)), out _).ShouldBeFalse();
        DetailFieldRules.TryIban(WithCheckDigits("LC", new string('1', DetailFieldRules.IbanMaxChars - 3)), out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("GB83WEST12345698765432")] // one check digit off
    [InlineData("GB82WEST12345698765433")] // one account digit off
    [InlineData("GB82TSEW12345698765432")] // letters swapped
    [InlineData("12GBWEST12345698765432")] // country is not letters
    [InlineData("GBXXWEST12345698765432")] // check digits are not digits
    [InlineData("GB82-WEST-1234-5698-7654-32")]
    [InlineData("GB82WEST1234569876543!")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void A_bad_iban_is_refused(string? text) =>
        DetailFieldRules.TryIban(text, out _).ShouldBeFalse();

    [Fact]
    public void Letters_and_digits_outside_ascii_are_refused()
    {
        DetailFieldRules.TryIban(WithCheckDigits("DE", "37040044053201300").Replace("37", "\u0663\u0667", StringComparison.Ordinal), out _).ShouldBeFalse(); // Arabic-Indic digits
        DetailFieldRules.TryIban("\u00c4" + "E89370400440532013000", out _).ShouldBeFalse(); // A with diaeresis
    }

    // --- Recipient name and payment purpose ------------------------------------------------------------

    public static TheoryData<string> TextFields() => ["recipient_name", "payment_purpose"];

    [Theory]
    [MemberData(nameof(TextFields))]
    public void Whitespace_runs_collapse_to_one_space(string field)
    {
        Text(field, "  Ivan   Petrov ", out var text).ShouldBeTrue();
        text.ShouldBe("Ivan Petrov");
        Text(field, "Ivan\tPetrov\nJr", out text).ShouldBeTrue();
        text.ShouldBe("Ivan Petrov Jr");
        Text(field, "Ivan\u00a0Petrov", out text).ShouldBeTrue(); // a no-break space is whitespace
        text.ShouldBe("Ivan Petrov");
    }

    [Theory]
    [MemberData(nameof(TextFields))]
    public void Text_up_to_the_limit_is_accepted_and_one_more_is_not(string field)
    {
        Text(field, new string('a', DetailFieldRules.TextMaxChars), out _).ShouldBeTrue();
        Text(field, new string('a', DetailFieldRules.TextMaxChars + 1), out _).ShouldBeFalse();
    }

    [Theory]
    [MemberData(nameof(TextFields))]
    public void Characters_are_counted_as_characters_not_utf16_units(string field)
    {
        var emoji = char.ConvertFromUtf32(0x1F600); // two UTF-16 units, one character
        Text(field, new string('a', DetailFieldRules.TextMaxChars - 1) + emoji, out _).ShouldBeTrue();
        Text(field, new string('a', DetailFieldRules.TextMaxChars) + emoji, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("recipient_name", "")]
    [InlineData("recipient_name", " \t\n ")]
    [InlineData("recipient_name", null)]
    [InlineData("recipient_name", "Ivan\u0000Petrov")] // NUL
    [InlineData("recipient_name", "Ivan\u0007")] // bell
    [InlineData("recipient_name", "Ivan\u007f")] // delete
    [InlineData("recipient_name", "Ivan\u202ePetrov")] // right-to-left override: reorders what the operator reads
    [InlineData("recipient_name", "Ivan\u200bPetrov")] // zero-width space
    [InlineData("payment_purpose", "")]
    [InlineData("payment_purpose", "Invoice\u0000")]
    [InlineData("payment_purpose", "Invoice\u202e")]
    public void Empty_or_control_text_is_refused(string field, string? value) =>
        Text(field, value, out _).ShouldBeFalse();

    [Fact]
    public void A_lone_surrogate_is_refused()
    {
        // Built at run time: test data passed through attributes turns a lone surrogate into U+FFFD.
        const char HighSurrogate = (char)0xD800;
        const char LowSurrogate = (char)0xDC00;

        DetailFieldRules.TryRecipientName("Ivan" + HighSurrogate, out _).ShouldBeFalse();
        DetailFieldRules.TryRecipientName("Ivan" + LowSurrogate + "Petrov", out _).ShouldBeFalse();
        DetailFieldRules.TryPaymentPurpose(HighSurrogate + "Invoice", out _).ShouldBeFalse();
    }

    [Fact]
    public void Text_in_any_script_is_accepted()
    {
        DetailFieldRules.TryRecipientName("\u0418\u0432\u0430\u043d \u041f\u0435\u0442\u0440\u043e\u0432", out var name).ShouldBeTrue(); // Cyrillic
        name.ShouldBe("\u0418\u0432\u0430\u043d \u041f\u0435\u0442\u0440\u043e\u0432");
        DetailFieldRules.TryPaymentPurpose("Rechnung Nr. 12/2026 \u2013 M\u00fcller", out _).ShouldBeTrue();
    }

    // --- Wallet address ----------------------------------------------------------------------------

    [Theory]
    [InlineData("TQn9Y2khEsLJW1ChVWFMSMeRDow5KcbLSE")] // TRC20, base58
    [InlineData("0x52908400098527886E0F7030069857D2E4169EE7")] // ERC20, 0x-hex
    [InlineData("EQDtFpEwcFAEcRe5mLVh2N6C0x-_hJEM7W61_JLnSF74p4q2")] // TON, base64url
    [InlineData("UQBvW8Z5huBkMJYdnfAEM5JqTNkuWX3diqYENkWsIL0XggGG")]
    [InlineData("0:83dfd552e63729b472fcbcc8c45ebcc6691702558b68ec7527e1ba403a0f31a8")] // TON raw
    [InlineData("abc+def/ghi=jkl:mno_pqr-stu")] // every allowed punctuation mark
    public void A_wallet_address_in_any_supported_form_is_accepted(string address)
    {
        DetailFieldRules.TryWalletAddress(address, out var accepted).ShouldBeTrue();
        accepted.ShouldBe(address);
    }

    [Fact]
    public void A_wallet_address_is_trimmed()
    {
        DetailFieldRules.TryWalletAddress("  TQn9Y2khEsLJW1ChVWFMSMeRDow5KcbLSE\n", out var address).ShouldBeTrue();
        address.ShouldBe("TQn9Y2khEsLJW1ChVWFMSMeRDow5KcbLSE");
    }

    [Fact]
    public void Wallet_addresses_at_the_length_bounds()
    {
        DetailFieldRules.TryWalletAddress(new string('a', DetailFieldRules.WalletMinChars), out _).ShouldBeTrue();
        DetailFieldRules.TryWalletAddress(new string('a', DetailFieldRules.WalletMaxChars), out _).ShouldBeTrue();
        DetailFieldRules.TryWalletAddress(new string('a', DetailFieldRules.WalletMinChars - 1), out _).ShouldBeFalse();
        DetailFieldRules.TryWalletAddress(new string('a', DetailFieldRules.WalletMaxChars + 1), out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("TQn9Y2khEsLJW1Ch VWFMSMeRDow5KcbLSE")] // an inner space
    [InlineData("TQn9Y2khEsLJW1ChVWFMSMeRDow5KcbLS!")]
    [InlineData("TQn9Y2khEsLJW1ChVWFMSMeRDow5KcbL\u00e9")]
    [InlineData("TQn9Y2khEsLJW1ChVWFMSMeRDow5Kcb\u0661\u0662")] // Arabic-Indic digits
    [InlineData("")]
    [InlineData(null)]
    public void A_bad_wallet_address_is_refused(string? address) =>
        DetailFieldRules.TryWalletAddress(address, out _).ShouldBeFalse();

    // --- Masking (spec 1.5) ---------------------------------------------------------------------------

    [Theory]
    [InlineData("ME25505000012345678951", "ME25...8951")]
    [InlineData("123456789", "1234...6789")] // nine: the shortest that keeps its ends
    [InlineData("12345678", "...")] // eight or fewer: the ellipsis alone
    [InlineData("1", "...")]
    [InlineData("", "...")]
    public void A_value_is_masked_to_its_ends(string value, string masked) =>
        DetailFieldRules.Mask(value).ShouldBe(masked);

    [Fact]
    public void A_missing_value_stays_missing() => DetailFieldRules.Mask(null).ShouldBeNull();

    private static bool Text(string field, string? value, out string text) =>
        field == "recipient_name" ? DetailFieldRules.TryRecipientName(value, out text) : DetailFieldRules.TryPaymentPurpose(value, out text);

    // An IBAN-like string whose two check characters are letters yet whose MOD 97-10 still holds:
    // the last account digit is searched until it does.
    private static string WithLetterCheckDigits(string country, string account)
    {
        for (var last = 0; last < 1000; last++)
        {
            var candidate = country + "AB" + account + last.ToString("000", CultureInfo.InvariantCulture);
            if (Remainder(candidate[4..] + candidate[..4]) == 1)
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("no candidate found");
    }

    private static int Remainder(string rearranged) =>
        (int)(BigInteger.Parse(string.Concat(rearranged.Select(c => char.IsAsciiLetter(c) ? (c - 'A' + 10).ToString(CultureInfo.InvariantCulture) : c.ToString())), CultureInfo.InvariantCulture) % 97);

    // ISO 7064 MOD 97-10, independently of the code under test: the check digits make the
    // rearranged number leave remainder 1.
    private static string WithCheckDigits(string country, string account)
    {
        var digits = string.Concat((account + country + "00").Select(c => char.IsAsciiLetter(c) ? (c - 'A' + 10).ToString(CultureInfo.InvariantCulture) : c.ToString()));
        var check = 98 - (int)(BigInteger.Parse(digits, CultureInfo.InvariantCulture) % 97);
        return country + check.ToString("00", CultureInfo.InvariantCulture) + account;
    }
}
