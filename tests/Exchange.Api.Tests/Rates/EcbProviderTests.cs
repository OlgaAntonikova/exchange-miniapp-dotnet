using System.Net;
using System.Text;
using Exchange.Api.Rates;
using Exchange.Domain.Directions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Exchange.Api.Tests.Rates;

/// <summary>
/// Spec 1.4 and INV-5: the ECB provider against a fake endpoint serving the observed
/// <c>eurofxref-daily.xml</c> shape with made-up numbers. The live endpoint is checked at the
/// PHASE-3 gate.
/// </summary>
public sealed class EcbProviderTests
{
    private const string UsdPerEur = "1.1177";
    private const int MaxEffectiveAgeDays = 5;

    private static readonly Uri Url = new("https://ecb.example.test/eurofxref-daily.xml");

    // 2026-10-07 09:00 UTC is 10:00 in Frankfurt (fixed UTC+1), the same calendar day.
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Usd_is_one_and_eur_is_the_inverse_of_usd_per_eur()
    {
        var snapshot = await Fetch(Document());

        snapshot.Provider.ShouldBe("ecb");
        snapshot.Mids.Keys.ShouldBe([Currency.Usd, Currency.Eur], ignoreOrder: true);
        snapshot.Mids[Currency.Usd].ShouldBe(1m);
        snapshot.Mids[Currency.Eur].ShouldBe(1m / 1.1177m);
        snapshot.TakenAt.ShouldBe(Now);
    }

    [Fact]
    public async Task The_snapshot_is_valid_until_the_end_of_the_last_day_of_its_window()
    {
        var snapshot = await Fetch(Document("2026-10-07"));

        // Effective 7 October, 5 days allowed: usable through 12 October, Frankfurt time.
        snapshot.ValidUntil.ShouldBe(new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.FromHours(1)));
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
    [InlineData("2026-10-07", null)] // today
    [InlineData("2026-10-02", null)] // 5 days behind: the last allowed
    [InlineData("2026-10-01", "date_too_old")]
    [InlineData("2026-10-08", "date_in_the_future")]
    public async Task The_reported_date_must_be_today_or_at_most_five_days_behind(string date, string? reason)
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
    public async Task Today_is_taken_in_frankfurt_at_utc_plus_one()
    {
        // 23:30 UTC on 7 October is 00:30 on 8 October in Frankfurt: a rate dated the 8th is not
        // in the future there, though it is in UTC.
        var lateEvening = new DateTimeOffset(2026, 10, 7, 23, 30, 0, TimeSpan.Zero);

        (await Fetch(Document("2026-10-08"), lateEvening)).Mids.ShouldNotBeEmpty();
        (await Refusal(Document("2026-10-09"), lateEvening)).ShouldBe("date_in_the_future");
    }

    // --- The document --------------------------------------------------------------------------------

    public static TheoryData<string, string> BadDocuments() => new()
    {
        { "not xml", "malformed_xml" },
        { "<?xml version=\"1.0\"?><ValCurs Date=\"07.10.2026\"/>", "unexpected_shape" },
        { Envelope(""), "unexpected_shape" }, // no dated cube
        { "<Envelope xmlns=\"http://www.ecb.int/vocabulary/2002-08-01/eurofxref\"><Cube>" + Dated("2026-10-07", Rate("USD", UsdPerEur)) + "</Cube></Envelope>", "unexpected_shape" }, // not the gesmes envelope
        { Envelope(Dated("2026-10-07", Rate("USD", UsdPerEur)) + Dated("2026-10-06", Rate("USD", UsdPerEur))), "unexpected_shape" }, // two
        { Envelope(Dated("2026-10-07", Rate("JPY", "168.21"))), "missing_USD" },
        { Envelope(Dated("2026-10-07", Rate("USD", UsdPerEur) + Rate("USD", "1.2"))), "duplicate_USD" },
        { Envelope(Dated("2026-10-07", Rate("USD", ""))), "bad_rate_USD" },
        { Envelope(Dated("2026-10-07", Rate("USD", "0"))), "bad_rate_USD" },
        { Envelope(Dated("2026-10-07", Rate("USD", "0.0"))), "bad_rate_USD" },
        { Envelope(Dated("2026-10-07", Rate("USD", "-1.1"))), "bad_rate_USD" },
        { Envelope(Dated("2026-10-07", Rate("USD", "+1.1"))), "bad_rate_USD" },
        { Envelope(Dated("2026-10-07", Rate("USD", "1e3"))), "bad_rate_USD" },
        { Envelope(Dated("2026-10-07", Rate("USD", "1,1"))), "bad_rate_USD" },
        { Envelope(Dated("2026-10-07", Rate("USD", " 1.1"))), "bad_rate_USD" },
        { Envelope(Dated("2026-10-07", Rate("USD", "NaN"))), "bad_rate_USD" },
        { Envelope(Dated("2026-10-07", Rate("USD", ".5"))), "bad_rate_USD" },
        { Envelope(Dated("2026-10-07", Rate("USD", "1."))), "bad_rate_USD" },
        { Envelope(Dated("2026-10-07", Rate("USD", "\u0661.\u0661"))), "bad_rate_USD" }, // Arabic-Indic digits
        { Envelope(Dated("2026-10-07", Rate("USD", "99999999999999999999999999999999"))), "bad_rate_USD" }, // beyond decimal
        { Envelope(Dated("2026-13-01", Rate("USD", UsdPerEur))), "bad_date" },
        { Envelope(Dated("20261007", Rate("USD", UsdPerEur))), "bad_date" },
        { Envelope(Dated("2026-10-7", Rate("USD", UsdPerEur))), "bad_date" },
        { Envelope(Dated("", Rate("USD", UsdPerEur))), "bad_date" },
    };

    [Theory]
    [MemberData(nameof(BadDocuments))]
    public async Task A_bad_document_is_refused(string body, string reason) =>
        (await Refusal(Encoding.UTF8.GetBytes(body))).ShouldBe(reason);

    [Theory]
    [InlineData("""<?xml version="1.0"?><!DOCTYPE lolz [<!ENTITY lol "lol"><!ENTITY lol2 "&lol;&lol;&lol;&lol;&lol;">]><gesmes:Envelope xmlns:gesmes="http://www.gesmes.org/xml/2002-08-01">&lol2;</gesmes:Envelope>""")]
    [InlineData("""<?xml version="1.0"?><!DOCTYPE x [<!ENTITY x SYSTEM "file:///etc/passwd">]><gesmes:Envelope xmlns:gesmes="http://www.gesmes.org/xml/2002-08-01">&x;</gesmes:Envelope>""")]
    [InlineData("""<?xml version="1.0"?><!DOCTYPE gesmes:Envelope><gesmes:Envelope xmlns:gesmes="http://www.gesmes.org/xml/2002-08-01"/>""")]
    public async Task A_document_with_a_dtd_is_refused(string body) =>
        (await Refusal(Encoding.UTF8.GetBytes(body))).ShouldBe("malformed_xml"); // D-012

    // --- The transport -----------------------------------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "http_500")]
    [InlineData(HttpStatusCode.NotFound, "http_404")]
    [InlineData(HttpStatusCode.NoContent, "http_204")]
    public async Task A_non_200_answer_is_refused(HttpStatusCode status, string reason) =>
        (await Refusal(StubHttp.Serving(Document(), status))).ShouldBe(reason);

    [Fact]
    public async Task A_transport_failure_names_its_type_and_not_the_url()
    {
        var error = await RefusalError(StubHttp.Throwing(new HttpRequestException("connection refused to " + Url)));

        error.Reason.ShouldBe("transport_HttpRequestException");
        error.ToString().ShouldNotContain("ecb.example.test");
        error.InnerException.ShouldBeNull();
    }

    [Fact]
    public async Task A_timeout_is_refused_as_a_timeout()
    {
        var error = await RefusalError(StubHttp.Throwing(new TaskCanceledException("timed out on " + Url, new TimeoutException())));

        error.Reason.ShouldBe("timeout");
        error.ToString().ShouldNotContain("ecb.example.test");
    }

    [Fact]
    public async Task A_cancelled_caller_is_not_a_failed_fetch()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var http = new StubHttp(_ => throw new TaskCanceledException());

        await Should.ThrowAsync<OperationCanceledException>(() => Provider(http).FetchAsync(cancelled.Token));
    }

    internal static string Rate(string code, string value) => $"<Cube currency='{code}' rate='{value}'/>";

    internal static string Dated(string date, string rows) => $"<Cube time='{date}'>{rows}</Cube>";

    internal static string Envelope(string dated) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
        + "<gesmes:Envelope xmlns:gesmes=\"http://www.gesmes.org/xml/2002-08-01\" xmlns=\"http://www.ecb.int/vocabulary/2002-08-01/eurofxref\">"
        + "<gesmes:subject>Reference rates</gesmes:subject>"
        + "<gesmes:Sender><gesmes:name>European Central Bank</gesmes:name></gesmes:Sender>"
        + $"<Cube>{dated}</Cube></gesmes:Envelope>";

    internal static byte[] Document(string date = "2026-10-07") =>
        Encoding.UTF8.GetBytes(Envelope(Dated(date, Rate("JPY", "168.21") + Rate("USD", UsdPerEur) + Rate("GBP", "0.8634"))));

    private static EcbProvider Provider(StubHttp http, DateTimeOffset? now = null) =>
#pragma warning disable CA2000 // the client owns the handler; both live as long as the test
        new(new HttpClient(http), Url, MaxEffectiveAgeDays, new FakeTimeProvider(now ?? Now));
#pragma warning restore CA2000

    private static Task<RateSnapshot> Fetch(byte[] body, DateTimeOffset? now = null) =>
        Provider(StubHttp.Serving(body), now).FetchAsync(TestContext.Current.CancellationToken);

    private static async Task<string> Refusal(byte[] body, DateTimeOffset? now = null) =>
        (await RefusalError(StubHttp.Serving(body), now)).Reason;

    private static async Task<string> Refusal(StubHttp http) => (await RefusalError(http)).Reason;

    private static Task<RateProviderException> RefusalError(StubHttp http, DateTimeOffset? now = null) =>
        Should.ThrowAsync<RateProviderException>(() => Provider(http, now).FetchAsync(TestContext.Current.CancellationToken));
}
