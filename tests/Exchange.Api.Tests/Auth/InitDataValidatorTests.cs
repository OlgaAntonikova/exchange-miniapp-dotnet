using System.Security.Cryptography;
using System.Text;
using Exchange.Api.Auth;
using Exchange.Api.Settings;
using Shouldly;

namespace Exchange.Api.Tests.Auth;

/// <summary>
/// INV-1 and INV-2 (RULE-TEST-FIRST): an initData is accepted only when Telegram's HMAC matches for
/// this bot and its auth_date is recent; everything else is refused with a reason code and nothing
/// of the input. The signing below is written from Telegram's documentation, independently of the
/// validator.
/// </summary>
public sealed class InitDataValidatorTests
{
    private const int MaxAgeSeconds = 86_400;
    private const int TokenTailChars = 35;
    private const long Now = 1_760_000_000;

    private static readonly Secret BotToken = new("7777777" + ":" + new string('T', TokenTailChars));
    private static readonly Secret OtherToken = new("7777777" + ":" + new string('U', TokenTailChars));
    private static readonly DateTimeOffset NowTime = DateTimeOffset.FromUnixTimeSeconds(Now);

    private const string UserJson = "{\"id\":424242,\"first_name\":\"Ivan\",\"last_name\":\"P\",\"username\":\"ivan_p\",\"language_code\":\"ru\"}";

    [Fact]
    public void A_signed_recent_init_data_yields_its_user()
    {
        var result = Validate(Sign(Fields()));

        result.User.ShouldBe(new TelegramUser(424242, "Ivan", "ivan_p"));
        result.AuthDate.ShouldBe(Now - 10);
    }

    [Fact]
    public void A_user_without_a_username_is_accepted()
    {
        var fields = Fields();
        fields["user"] = "{\"id\":5,\"first_name\":\"Ann\"}";

        Validate(Sign(fields)).User.ShouldBe(new TelegramUser(5, "Ann", null));
    }

    [Fact]
    public void Values_are_signed_url_decoded_whichever_encoding_the_client_used()
    {
        var fields = Fields();
        fields["query_id"] = "AA BB+CC&DD=EE";

        Validate(Sign(fields, plusForSpace: true)).User.Id.ShouldBe(424242);
        Validate(Sign(fields, plusForSpace: false)).User.Id.ShouldBe(424242);
    }

    [Fact]
    public void Fields_are_sorted_by_byte_order_as_telegram_signs_them()
    {
        // Upper case sorts before lower case byte-wise ("Zeta" < "auth_date"); a case-insensitive
        // sort would build another data-check-string and refuse a valid signature.
        var fields = Fields();
        fields["Zeta"] = "1";

        Validate(Sign(fields)).User.Id.ShouldBe(424242);
    }

    // --- INV-1: the signature ------------------------------------------------------------------

    [Fact]
    public void A_byte_altered_value_is_refused() =>
        Reason(Sign(Fields()).Replace("ivan_p", "ivan_q", StringComparison.Ordinal)).ShouldBe("bad_signature");

    [Fact]
    public void An_added_field_is_refused() =>
        Reason(Sign(Fields()) + "&extra=1").ShouldBe("bad_signature");

    [Fact]
    public void Another_bots_signature_is_refused() =>
        Reason(Sign(Fields(), token: OtherToken)).ShouldBe("bad_signature");

    [Fact]
    public void A_hash_in_upper_case_is_refused()
    {
        var raw = Sign(Fields());
        var hash = raw[(raw.IndexOf("hash=", StringComparison.Ordinal) + "hash=".Length)..];

        Reason(raw.Replace(hash, hash.ToUpperInvariant(), StringComparison.Ordinal)).ShouldBe("bad_signature");
    }

    [Theory]
    [InlineData("")]
    [InlineData("&hash=")]
    public void A_missing_or_empty_hash_is_refused(string tail) =>
        Reason(Unsigned(Fields()) + tail).ShouldBe("missing_hash");

    // --- INV-2: the age --------------------------------------------------------------------------

    [Theory]
    [InlineData(MaxAgeSeconds, true)]
    [InlineData(MaxAgeSeconds + 1, false)]
    public void An_init_data_older_than_the_maximum_age_is_refused(long ageSeconds, bool accepted)
    {
        var fields = Fields();
        fields["auth_date"] = (Now - ageSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (accepted)
        {
            Validate(Sign(fields)).AuthDate.ShouldBe(Now - ageSeconds);
        }
        else
        {
            Reason(Sign(fields)).ShouldBe("expired");
        }
    }

    [Theory]
    [InlineData(InitDataValidator.InitDataMaxClockSkewSeconds, true)]
    [InlineData(InitDataValidator.InitDataMaxClockSkewSeconds + 1, false)]
    public void An_init_data_from_the_future_beyond_the_skew_is_refused(long aheadSeconds, bool accepted)
    {
        var fields = Fields();
        fields["auth_date"] = (Now + aheadSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (accepted)
        {
            Validate(Sign(fields)).AuthDate.ShouldBe(Now + aheadSeconds);
        }
        else
        {
            Reason(Sign(fields)).ShouldBe("from_the_future");
        }
    }

    // --- Shape -----------------------------------------------------------------------------------

    [Fact]
    public void An_oversized_input_is_refused_before_parsing() =>
        Reason(new string('a', InitDataValidator.MaxInitDataChars + 1)).ShouldBe("too_long");

    [Fact]
    public void An_input_of_exactly_the_maximum_length_is_accepted()
    {
        var fields = Fields();
        fields["pad"] = string.Empty;
        fields["pad"] = new string('a', InitDataValidator.MaxInitDataChars - Sign(fields).Length);
        var raw = Sign(fields);

        raw.Length.ShouldBe(InitDataValidator.MaxInitDataChars);
        Validate(raw).User.Id.ShouldBe(424242);
    }

    [Theory]
    [InlineData("auth_date=1&user=\u00e9&hash=00")] // a raw non-ASCII character
    [InlineData("auth_date=1&user=%ZZ&hash=00")] // a broken escape
    [InlineData("auth_date=1&user=%FF&hash=00")] // not UTF-8
    [InlineData("auth_date=1&&hash=00")] // an empty segment
    [InlineData("auth_date=1&user&hash=00")] // a segment without '='
    [InlineData("=1&hash=00")] // an empty key
    public void A_malformed_query_is_refused(string raw) => Reason(raw).ShouldBe("malformed");

    [Fact]
    public void A_field_sent_twice_is_refused() =>
        Reason(Sign(Fields()) + "&auth_date=1").ShouldBe("duplicate_field");

    [Theory]
    [InlineData(null, "missing_auth_date")]
    [InlineData("", "bad_auth_date")]
    [InlineData("12a", "bad_auth_date")]
    [InlineData("-5", "bad_auth_date")]
    [InlineData("+5", "bad_auth_date")]
    [InlineData("99999999999999999999", "bad_auth_date")]
    public void A_missing_or_malformed_auth_date_is_refused(string? authDate, string reason)
    {
        var fields = Fields();
        if (authDate is null)
        {
            fields.Remove("auth_date");
        }
        else
        {
            fields["auth_date"] = authDate;
        }

        Reason(Sign(fields)).ShouldBe(reason);
    }

    [Theory]
    [InlineData(null, "missing_user")]
    [InlineData("not json", "bad_user")]
    [InlineData("[1,2]", "bad_user")]
    [InlineData("{\"first_name\":\"A\"}", "bad_user")]
    [InlineData("{\"id\":true,\"first_name\":\"A\"}", "bad_user")]
    [InlineData("{\"id\":\"5\",\"first_name\":\"A\"}", "bad_user")]
    [InlineData("{\"id\":0,\"first_name\":\"A\"}", "bad_user")]
    [InlineData("{\"id\":-5,\"first_name\":\"A\"}", "bad_user")]
    [InlineData("{\"id\":5.5,\"first_name\":\"A\"}", "bad_user")]
    [InlineData("{\"id\":5}", "bad_user")]
    [InlineData("{\"id\":5,\"first_name\":7}", "bad_user")]
    [InlineData("{\"id\":5,\"first_name\":\"A\",\"username\":7}", "bad_user")]
    public void A_missing_or_malformed_user_is_refused(string? user, string reason)
    {
        var fields = Fields();
        if (user is null)
        {
            fields.Remove("user");
        }
        else
        {
            fields["user"] = user;
        }

        Reason(Sign(fields)).ShouldBe(reason);
    }

    [Fact]
    public void A_refusal_carries_nothing_of_the_input_or_the_token()
    {
        var raw = Sign(Fields(), token: OtherToken);

        var error = Should.Throw<InitDataException>(() => Validate(raw));

        error.Message.ShouldBe("initData refused: bad_signature");
        error.ToString().ShouldNotContain("ivan_p");
        error.ToString().ShouldNotContain(BotToken.Reveal());
        error.ToString().ShouldNotContain(raw[(raw.IndexOf("hash=", StringComparison.Ordinal) + "hash=".Length)..]);
    }

    private static Dictionary<string, string> Fields() => new(StringComparer.Ordinal)
    {
        ["query_id"] = "AAHdF6IQAAAAAN0XohDhrOrc",
        ["user"] = UserJson,
        ["auth_date"] = (Now - 10).ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["signature"] = "v1-ed25519-signature-not-checked",
    };

    // Telegram's method: data-check-string = sorted key=value lines of every field but hash;
    // secret = HMAC-SHA256(key: "WebAppData", data: bot token); hash = hex HMAC-SHA256(secret, dcs).
    private static string Sign(Dictionary<string, string> fields, Secret? token = null, bool plusForSpace = false)
    {
        var dataCheckString = string.Join('\n', fields.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}"));
        var secret = HMACSHA256.HashData(Encoding.UTF8.GetBytes("WebAppData"), Encoding.UTF8.GetBytes((token ?? BotToken).Reveal()));
        var hash = Convert.ToHexStringLower(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(dataCheckString)));
        return Unsigned(fields, plusForSpace) + "&hash=" + hash;
    }

    private static string Unsigned(Dictionary<string, string> fields, bool plusForSpace = false) =>
        string.Join('&', fields.Select(pair => pair.Key + "=" + Encode(pair.Value, plusForSpace)));

    private static string Encode(string value, bool plusForSpace)
    {
        var escaped = Uri.EscapeDataString(value);
        return plusForSpace ? escaped.Replace("%20", "+", StringComparison.Ordinal) : escaped;
    }

    private static ValidatedInitData Validate(string raw) => InitDataValidator.Validate(raw, BotToken, MaxAgeSeconds, NowTime);

    private static string Reason(string raw) => Should.Throw<InitDataException>(() => Validate(raw)).Reason;
}
