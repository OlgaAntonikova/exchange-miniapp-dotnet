using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Exchange.Api.Rates;

/// <summary>
/// What both rate sources share: one GET whose failures become reason codes that never quote the
/// URL or the body, an XML parse that refuses any DTD (D-012), and the rules for numbers and the
/// effective-date window.
/// </summary>
internal static partial class RateSourceDocument
{
    private const string TimeoutReason = "timeout";

    /// <summary>The body of a 200 answer to a GET of <paramref name="url"/>.</summary>
    /// <exception cref="RateProviderException">The request failed or the status was not 200.</exception>
    /// <exception cref="OperationCanceledException">The caller cancelled.</exception>
    public static async Task<byte[]> GetAsync(HttpClient client, Uri url, CancellationToken cancellationToken)
    {
        string? failure;
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            }

            failure = "http_" + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient reports its own timeout as a cancellation the caller did not ask for.
            failure = TimeoutReason;
        }
        catch (HttpRequestException error)
        {
            // Not chained: the transport's message quotes the URL.
            failure = "transport_" + error.GetType().Name;
        }

        throw new RateProviderException(failure);
    }

    /// <summary>Parses <paramref name="reader"/> with no DTD, no entity and no external resource.</summary>
    /// <exception cref="RateProviderException"><c>malformed_xml</c>.</exception>
    public static XDocument Parse(TextReader reader)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        try
        {
            using var xml = XmlReader.Create(reader, settings);
            return XDocument.Load(xml);
        }
        catch (XmlException)
        {
            // Not chained: the parser's message can quote the document.
            throw new RateProviderException("malformed_xml");
        }
    }

    /// <summary>
    /// A positive decimal written as ASCII digits with an optional fraction after
    /// <paramref name="separator"/> (the source's own decimal mark, and no other).
    /// </summary>
    public static bool TryPositive(string? text, char separator, out decimal value)
    {
        value = 0m;
        if (text is null || (separator != '.' && text.Contains('.', StringComparison.Ordinal)))
        {
            return false;
        }

        var normalized = text.Replace(separator, '.');
        return PlainNumber().IsMatch(normalized)
            && decimal.TryParse(normalized, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value)
            && value > 0m;
    }

    /// <summary>
    /// Checks an effective date against today at a fixed UTC offset, and returns the first moment
    /// the window has closed: the start of the day after the last day it may be used.
    /// </summary>
    /// <exception cref="RateProviderException"><c>date_in_the_future</c> or <c>date_too_old</c>.</exception>
    public static DateTimeOffset Window(DateOnly effective, DateTimeOffset now, TimeSpan offset, int maxLeadDays, int maxAgeDays)
    {
        var today = DateOnly.FromDateTime(now.ToOffset(offset).DateTime);
        if (effective.DayNumber - today.DayNumber > maxLeadDays)
        {
            throw new RateProviderException("date_in_the_future");
        }

        if (today.DayNumber - effective.DayNumber > maxAgeDays)
        {
            throw new RateProviderException("date_too_old");
        }

        return new DateTimeOffset(effective.AddDays(maxAgeDays + 1).ToDateTime(TimeOnly.MinValue), offset);
    }

    // ASCII digits with an optional fraction: no sign, exponent, space, NaN or other script.
    [GeneratedRegex(@"\A[0-9]+(\.[0-9]+)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex PlainNumber();
}
