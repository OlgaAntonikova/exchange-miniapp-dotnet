using System.Text.Json;
using Exchange.Api.Auth;
using Exchange.Api.Locale;

namespace Exchange.Api.Pricing;

/// <summary>
/// What <c>POST /api/rate</c> and <c>POST /api/orders</c> share around pricing: reading the body,
/// and turning a refusal into the 422 or 503 of INV-21, logged at WARNING with its fields only.
/// </summary>
public static partial class PricingRefusal
{
    /// <summary>The largest request body read; a quote or an order is well under a kilobyte.</summary>
    public const int MaxBodyBytes = 16 * 1024;

    private const string LoggerCategory = "Exchange.Api.Pricing";

    /// <summary>
    /// The body as JSON, read up to <see cref="MaxBodyBytes"/> and parsed with duplicate properties
    /// refused (which copy would count is ambiguous).
    /// </summary>
    /// <exception cref="PricingException">An unreadable, oversized or duplicated body: <c>invalid_request</c> naming <c>body</c>.</exception>
    public static async Task<JsonDocument> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (await AuthEndpoints.ReadCappedAsync(request.Body, MaxBodyBytes, cancellationToken).ConfigureAwait(false) is not { } body)
        {
            throw new PricingException(PricingRequest.InvalidRequest, [PricingRequest.WholeBody]);
        }

        try
        {
            return JsonDocument.Parse(body, new JsonDocumentOptions { AllowDuplicateProperties = false });
        }
        catch (JsonException)
        {
            throw new PricingException(PricingRequest.InvalidRequest, [PricingRequest.WholeBody]);
        }
    }

    /// <summary>The 503 with the localized message for <c>rates_unavailable</c>, the 422 otherwise.</summary>
    public static IResult Result(HttpContext http, LocaleTexts texts, PricingException refused, string operation)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(texts);
        ArgumentNullException.ThrowIfNull(refused);
        LogRefused(http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(LoggerCategory), operation, refused.Kind, string.Join(", ", refused.Fields));
        return refused.Kind == Pricer.RatesUnavailable
            ? Results.Json(
                new Dictionary<string, string> { ["error"] = Pricer.RatesUnavailable, ["message"] = texts.Text(LocaleKeys.RatesUnavailable) },
                statusCode: StatusCodes.Status503ServiceUnavailable)
            : Results.Json(
                new Dictionary<string, object> { ["error"] = refused.Kind, ["fields"] = refused.Fields },
                statusCode: StatusCodes.Status422UnprocessableEntity);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "{Operation} refused: {Error} {Fields}")]
    private static partial void LogRefused(ILogger logger, string operation, string error, string fields);
}
