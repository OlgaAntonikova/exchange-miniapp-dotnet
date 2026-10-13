using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Exchange.Domain.Directions;

namespace Exchange.Api.Rates;

/// <summary>
/// The Bank of Russia's official daily rate, the source of RUB (spec 1.4a, NR-5).
/// <c>XML_daily.asp</c> answers windows-1251 XML: <c>ValCurs Date="dd.mm.yyyy"</c> holding
/// <c>Valute</c> rows with <c>CharCode</c>, <c>Nominal</c> and <c>Value</c> (decimal comma);
/// RUB per USD is the USD row's <c>Value / Nominal</c>. The rate is set for a calendar date and
/// stays in force until replaced: the snapshot is taken when fetched, and the date it reports may
/// lead today in Moscow (fixed UTC+3) by <c>CBR_RATE_MAX_EFFECTIVE_LEAD_DAYS</c> and lag it by
/// <c>CBR_RATE_MAX_EFFECTIVE_AGE_DAYS</c>.
/// </summary>
public sealed partial class CbrProvider(HttpClient client, Uri url, int maxEffectiveLeadDays, int maxEffectiveAgeDays, TimeProvider time) : IRateProvider
{
    /// <summary>The provider's name.</summary>
    public const string ProviderName = "cbr";

    /// <summary>Moscow has kept UTC+3 all year since 2014 (NR-5).</summary>
    public static readonly TimeSpan MoscowOffset = TimeSpan.FromHours(3);

    private const int Windows1251CodePage = 1251;
    private const string UsdCode = "USD";

    // The source's encoding, from the code pages that ship with .NET (spec 1.4a); the body is
    // decoded here, so the parser never chooses an encoding from the document.
    private static readonly Encoding Windows1251 = CodePagesEncodingProvider.Instance.GetEncoding(Windows1251CodePage)!;

    /// <inheritdoc />
    public string Name => ProviderName;

    /// <inheritdoc />
    public async Task<RateSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        var body = await RateSourceDocument.GetAsync(client, url, cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow();
        using var reader = new StringReader(Windows1251.GetString(body));
        var root = RateSourceDocument.Parse(reader).Root!;
        if (root.Name != "ValCurs")
        {
            throw new RateProviderException("unexpected_shape");
        }

        var effective = EffectiveDate((string?)root.Attribute("Date"));
        var rubPerUsd = RubPerUsd(root);
        var validUntil = RateSourceDocument.Window(effective, now, MoscowOffset, maxEffectiveLeadDays, maxEffectiveAgeDays);
        return new RateSnapshot(ProviderName, now, new Dictionary<Currency, decimal> { [Currency.Rub] = rubPerUsd }, validUntil);
    }

    // An exact parse: two-digit day and month, ASCII digits, no space, no other form.
    private static DateOnly EffectiveDate(string? text) =>
        DateOnly.TryParseExact(text, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw new RateProviderException("bad_date");

    private static decimal RubPerUsd(XElement root)
    {
        var rows = root.Elements("Valute").Where(row => (string?)row.Element("CharCode") == UsdCode).ToList();
        if (rows.Count != 1)
        {
            throw new RateProviderException(rows.Count == 0 ? "missing_USD" : "duplicate_USD");
        }

        // The nominal is a whole number of units; the value carries the decimal comma.
        var nominalText = (string?)rows[0].Element("Nominal");
        return nominalText is not null && WholeNumber().IsMatch(nominalText)
            && RateSourceDocument.TryPositive(nominalText, ',', out var nominal)
            && RateSourceDocument.TryPositive((string?)rows[0].Element("Value"), ',', out var value)
            ? value / nominal
            : throw new RateProviderException("bad_rate_USD");
    }

    [GeneratedRegex(@"\A[0-9]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex WholeNumber();
}
