using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Exchange.Domain.Directions;

namespace Exchange.Api.Rates;

/// <summary>
/// The European Central Bank's euro reference rates, the source of USD and EUR (spec 1.4).
/// <c>eurofxref-daily.xml</c> is a gesmes envelope holding one <c>Cube time="yyyy-mm-dd"</c> of
/// <c>Cube currency rate</c> rows, each in units per one EUR; the engine works per one USD, so
/// <c>mid(USD) = 1</c> and <c>mid(EUR) = 1 / (USD per EUR)</c>. The rates stay in force until the
/// next publication: the snapshot is taken when fetched, and the date it reports must be today in
/// Frankfurt (fixed UTC+1) or at most <c>ECB_RATE_MAX_EFFECTIVE_AGE_DAYS</c> behind (NR-7).
/// </summary>
public sealed class EcbProvider(HttpClient client, Uri url, int maxEffectiveAgeDays, TimeProvider time) : IRateProvider
{
    /// <summary>The provider's name.</summary>
    public const string ProviderName = "ecb";

    /// <summary>Frankfurt's standard time; summer time moves it an hour, which never crosses midnight near a 16:00 publication.</summary>
    public static readonly TimeSpan FrankfurtOffset = TimeSpan.FromHours(1);

    private const int MaxEffectiveLeadDays = 0;
    private const string UsdCode = "USD";
    private static readonly XNamespace Gesmes = "http://www.gesmes.org/xml/2002-08-01";
    private static readonly XNamespace EuroFxRef = "http://www.ecb.int/vocabulary/2002-08-01/eurofxref";

    /// <inheritdoc />
    public string Name => ProviderName;

    /// <inheritdoc />
    public async Task<RateSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        var body = await RateSourceDocument.GetAsync(client, url, cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow();
        using var reader = new StreamReader(new MemoryStream(body), Encoding.UTF8);
        var root = RateSourceDocument.Parse(reader).Root!;
        if (root.Name != Gesmes + "Envelope")
        {
            throw new RateProviderException("unexpected_shape");
        }

        var dated = root.Elements(EuroFxRef + "Cube").Elements(EuroFxRef + "Cube").ToList();
        if (dated.Count != 1)
        {
            throw new RateProviderException("unexpected_shape");
        }

        var effective = ReferenceDate((string?)dated[0].Attribute("time"));
        var usdPerEur = UsdPerEur(dated[0]);
        var validUntil = RateSourceDocument.Window(effective, now, FrankfurtOffset, MaxEffectiveLeadDays, maxEffectiveAgeDays);
        return new RateSnapshot(
            ProviderName,
            now,
            new Dictionary<Currency, decimal> { [Currency.Usd] = 1m, [Currency.Eur] = 1m / usdPerEur },
            validUntil);
    }

    // An exact parse: two-digit month and day, ASCII digits, no space, no other form.
    private static DateOnly ReferenceDate(string? text) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw new RateProviderException("bad_date");

    private static decimal UsdPerEur(XElement dated)
    {
        var rows = dated.Elements(EuroFxRef + "Cube").Where(row => (string?)row.Attribute("currency") == UsdCode).ToList();
        if (rows.Count != 1)
        {
            throw new RateProviderException(rows.Count == 0 ? "missing_USD" : "duplicate_USD");
        }

        return RateSourceDocument.TryPositive((string?)rows[0].Attribute("rate"), '.', out var rate)
            ? rate
            : throw new RateProviderException("bad_rate_USD");
    }
}
