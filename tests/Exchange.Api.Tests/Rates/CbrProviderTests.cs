using System.Net;
using System.Text;
using Exchange.Api.Rates;
using Exchange.Domain.Directions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Exchange.Api.Tests.Rates;

/// <summary>
/// Spec 1.4a and INV-5: the Bank of Russia provider against a fake endpoint serving the observed
/// <c>XML_daily.asp</c> shape (NR-5) in windows-1251 with made-up numbers. The live endpoint is
/// checked at the PHASE-3 gate.
/// </summary>
public sealed class CbrProviderTests
{
    private const int MaxEffectiveLeadDays = 1;
    private const int MaxEffectiveAgeDays = 15;
    private const string DollarName = "\u0414\u043e\u043b\u043b\u0430\u0440 \u0421\u0428\u0410"; // "US dollar" in Russian: not ASCII

    private static readonly Uri Url = new("https://cbr.example.test/scripts/XML_daily.asp");

    // 2026-10-07 09:00 UTC is 12:00 in Moscow (UTC+3), the same calendar day.
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);

    private const int Windows1251CodePage = 1251;

    private static readonly Encoding Windows1251 = CodePagesEncodingProvider.Instance.GetEncoding(Windows1251CodePage)!;

    [Fact]
    public async Task Rub_per_usd_is_the_usd_value_with_its_decimal_comma()
    {
        var snapshot = await Fetch(Document());

        snapshot.Provider.ShouldBe("cbr");
        snapshot.Mids.Keys.ShouldBe([Currency.Rub]);
        snapshot.Mids[Currency.Rub].ShouldBe(78.5075m);
        snapshot.TakenAt.ShouldBe(Now);
    }

    [Fact]
    public async Task The_value_is_divided_by_its_nominal()
    {
        var snapshot = await Fetch(Document(rows: Valute("USD", "10", "785,075")));

        snapshot.Mids[Currency.Rub].ShouldBe(78.5075m);
    }

    [Fact]
    public async Task The_snapshot_is_valid_until_the_end_of_the_last_day_of_its_window()
    {
        var snapshot = await Fetch(Document("07.10.2026"));

        // Effective 7 October, 15 days allowed: usable through 22 October, Moscow time.
        snapshot.ValidUntil.ShouldBe(new DateTimeOffset(2026, 10, 23, 0, 0, 0, TimeSpan.FromHours(3)));
    }

    [Fact]
    public async Task It_gets_the_configured_url()
    {
        var http = StubHttp.Serving(Document());

        await Provider(http).FetchAsync(TestContext.Current.CancellationToken);

        var request = http.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe(HttpMethod.Get);
        request.RequestUri.ShouldBe(Url);
    }

    // --- The effective-date window (INV-5) ---------------------------------------------------------

    [Theory]
    [InlineData("07.10.2026", null)] // today
    [InlineData("08.10.2026", null)] // one day ahead: the next business day's rate, published today
    [InlineData("09.10.2026", "date_in_the_future")]
    [InlineData("22.09.2026", null)] // 15 days behind: the last allowed
    [InlineData("21.09.2026", "date_too_old")]
    public async Task The_effective_date_may_lead_by_one_day_and_lag_by_fifteen(string date, string? reason)
    {
        if (reason is null)
        {
            (await Fetch(Document(date))).Mids.ShouldNotBeEmpty();
        }
        else
        {
            (await Refusal(Document(date))).ShouldBe(reason);
        }
    }

    [Fact]
    public async Task Today_is_taken_in_moscow_at_utc_plus_three()
    {
        // 21:30 UTC on 7 October is 00:30 on 8 October in Moscow: a rate dated the 9th leads by
        // one day there, though by two in UTC.
        var lateEvening = new DateTimeOffset(2026, 10, 7, 21, 30, 0, TimeSpan.Zero);

        (await Fetch(Document("09.10.2026"), lateEvening)).Mids.ShouldNotBeEmpty();
        (await Refusal(Document("10.10.2026"), lateEvening)).ShouldBe("date_in_the_future");
    }

    // --- The document --------------------------------------------------------------------------------

    public static TheoryData<string, string> BadDocuments() => new()
    {
        { "not xml", "malformed_xml" },
        { EcbProviderTests.Envelope(EcbProviderTests.Dated("2026-10-07", EcbProviderTests.Rate("USD", "1.1"))), "unexpected_shape" },
        { Text("07.10.2026", Valute("JPY", "100", "52,1234")), "missing_USD" },
        { Text("07.10.2026", Valute("USD", "1", "78,5075") + Valute("USD", "1", "79,0000")), "duplicate_USD" },
        { Text("07.10.2026", Valute("USD", "1", "")), "bad_rate_USD" },
        { Text("07.10.2026", Valute("USD", "1", "0,0000")), "bad_rate_USD" },
        { Text("07.10.2026", Valute("USD", "1", "-78,5")), "bad_rate_USD" },
        { Text("07.10.2026", Valute("USD", "1", "78.5075")), "bad_rate_USD" }, // a decimal point is not this source's form
        { Text("07.10.2026", Valute("USD", "1", "78,50,75")), "bad_rate_USD" },
        { Text("07.10.2026", Valute("USD", "1", "7,85e1")), "bad_rate_USD" },
        { Text("07.10.2026", Valute("USD", "1", ",5")), "bad_rate_USD" },
        { Text("07.10.2026", Valute("USD", "1", "78,")), "bad_rate_USD" },
        { Text("07.10.2026", Valute("USD", "0", "78,5075")), "bad_rate_USD" },
        { Text("07.10.2026", Valute("USD", "1,5", "78,5075")), "bad_rate_USD" }, // a nominal is a whole number
        { Text("07.10.2026", Valute("USD", "", "78,5075")), "bad_rate_USD" },
        { Text("07.10.2026", "<Valute><CharCode>USD</CharCode><Value>78,5075</Value></Valute>"), "bad_rate_USD" }, // no nominal
        { Text("2026-10-07", Valute("USD", "1", "78,5075")), "bad_date" },
        { Text("32.10.2026", Valute("USD", "1", "78,5075")), "bad_date" },
        { Text("7.10.2026", Valute("USD", "1", "78,5075")), "bad_date" },
        { Text("", Valute("USD", "1", "78,5075")), "bad_date" },
        { "<?xml version=\"1.0\"?><ValCurs name=\"Foreign Currency Market\">" + Valute("USD", "1", "78,5075") + "</ValCurs>", "bad_date" },
    };

    [Theory]
    [MemberData(nameof(BadDocuments))]
    public async Task A_bad_document_is_refused(string body, string reason) =>
        (await Refusal(Windows1251.GetBytes(body))).ShouldBe(reason);

    [Theory]
    [InlineData("""<?xml version="1.0"?><!DOCTYPE lolz [<!ENTITY lol "lol"><!ENTITY lol2 "&lol;&lol;&lol;&lol;&lol;">]><ValCurs Date="07.10.2026">&lol2;</ValCurs>""")]
    [InlineData("""<?xml version="1.0"?><!DOCTYPE x [<!ENTITY x SYSTEM "file:///etc/passwd">]><ValCurs Date="07.10.2026">&x;</ValCurs>""")]
    public async Task A_document_with_a_dtd_is_refused(string body) =>
        (await Refusal(Windows1251.GetBytes(body))).ShouldBe("malformed_xml"); // D-012

    // --- The transport -----------------------------------------------------------------------------------

    [Fact]
    public async Task A_non_200_answer_is_refused() =>
        (await RefusalError(StubHttp.Serving(Document(), HttpStatusCode.ServiceUnavailable))).Reason.ShouldBe("http_503");

    [Fact]
    public async Task A_transport_failure_names_its_type_and_not_the_url()
    {
        var error = await RefusalError(StubHttp.Throwing(new HttpRequestException("connection refused to " + Url)));

        error.Reason.ShouldBe("transport_HttpRequestException");
        error.ToString().ShouldNotContain("cbr.example.test");
        error.InnerException.ShouldBeNull();
    }

    [Fact]
    public async Task A_timeout_is_refused_as_a_timeout() =>
        (await RefusalError(StubHttp.Throwing(new TaskCanceledException("timed out", new TimeoutException())))).Reason.ShouldBe("timeout");

    internal static string Valute(string code, string nominal, string value) =>
        $"<Valute ID=\"R0{code}\"><NumCode>840</NumCode><CharCode>{code}</CharCode><Nominal>{nominal}</Nominal>"
        + $"<Name>{DollarName}</Name><Value>{value}</Value><VunitRate>{value}</VunitRate></Valute>";

    internal static string Text(string date, string rows) =>
        $"<?xml version=\"1.0\" encoding=\"windows-1251\"?><ValCurs Date=\"{date}\" name=\"Foreign Currency Market\">{rows}</ValCurs>";

    internal static byte[] Document(string date = "07.10.2026", string? rows = null) =>
        Windows1251.GetBytes(Text(date, rows ?? Valute("JPY", "100", "52,1234") + Valute("USD", "1", "78,5075")));

    private static CbrProvider Provider(StubHttp http, DateTimeOffset? now = null) =>
#pragma warning disable CA2000 // the client owns the handler; both live as long as the test
        new(new HttpClient(http), Url, MaxEffectiveLeadDays, MaxEffectiveAgeDays, new FakeTimeProvider(now ?? Now));
#pragma warning restore CA2000

    private static Task<RateSnapshot> Fetch(byte[] body, DateTimeOffset? now = null) =>
        Provider(StubHttp.Serving(body), now).FetchAsync(TestContext.Current.CancellationToken);

    private static async Task<string> Refusal(byte[] body, DateTimeOffset? now = null) =>
        (await RefusalError(StubHttp.Serving(body), now)).Reason;

    private static Task<RateProviderException> RefusalError(StubHttp http, DateTimeOffset? now = null) =>
        Should.ThrowAsync<RateProviderException>(() => Provider(http, now).FetchAsync(TestContext.Current.CancellationToken));
}
