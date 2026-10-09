using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Exchange.Domain.Directions;

namespace Exchange.Api.Pricing;

/// <summary>A request's priced part, every value already in its domain type.</summary>
/// <param name="Give">What the client gives.</param>
/// <param name="Receive">What the client receives.</param>
/// <param name="AmountGive">The amount given, positive, at most two decimals.</param>
/// <param name="ReferenceIds">The reference ids present in the request, by field name.</param>
public sealed record PricingInput(GiveCode Give, ReceiveCode Receive, decimal AmountGive, IReadOnlyDictionary<string, int> ReferenceIds);

/// <summary>A request cannot be priced: an error kind of spec 1.10 and the <c>body.</c>-prefixed fields at fault.</summary>
public sealed class PricingException : Exception
{
    /// <summary>Creates the exception.</summary>
    public PricingException(string kind, IReadOnlyList<string> fields)
        : base("pricing refused: " + kind)
    {
        Kind = kind;
        Fields = fields;
    }

    /// <summary>Required by the exception pattern.</summary>
    public PricingException()
        : this("unknown", [])
    {
    }

    /// <summary>Required by the exception pattern.</summary>
    public PricingException(string message, Exception innerException)
        : base(message, innerException)
    {
        Kind = "unknown";
        Fields = [];
    }

    /// <summary><c>invalid_request</c>, <c>invalid_direction</c>, <c>amount_too_small</c> or <c>rates_unavailable</c>.</summary>
    public string Kind { get; }

    /// <summary>The fields at fault, each <c>body.</c>-prefixed.</summary>
    public IReadOnlyList<string> Fields { get; }
}

/// <summary>The request rules of spec 1.10 shared by <c>POST /api/rate</c> and <c>POST /api/orders</c>.</summary>
public static partial class PricingRequest
{
    /// <summary>The error kind of a request that breaks the rules.</summary>
    public const string InvalidRequest = "invalid_request";

    /// <summary>The field named when the body as a whole is unusable.</summary>
    public const string WholeBody = "body";

    /// <summary>The prefix of every field an error names.</summary>
    public const string FieldPrefix = "body.";

    private const string GiveField = "give";
    private const string ReceiveField = "receive";
    private const string AmountGiveField = "amount_give";

    // The order the reference ids are checked and listed in.
    private static readonly string[] ReferenceIdFields =
        [DetailFields.CityId, DetailFields.BankGiveId, DetailFields.BankReceiveId, DetailFields.NetworkGiveId, DetailFields.NetworkReceiveId];

    /// <summary>The direction, the amount and the reference ids of <paramref name="root"/>.</summary>
    /// <exception cref="PricingException"><c>invalid_request</c>, listing every field with a bad value.</exception>
    public static PricingInput Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new PricingException(InvalidRequest, [WholeBody]);
        }

        var bad = new List<string>();
        var give = Codes.TryParseGive(StringOf(root, GiveField), out var giveCode) ? giveCode : Bad<GiveCode>(bad, GiveField);
        var receive = Codes.TryParseReceive(StringOf(root, ReceiveField), out var receiveCode) ? receiveCode : Bad<ReceiveCode>(bad, ReceiveField);
        var amount = TryAmount(StringOf(root, AmountGiveField), out var amountGive) ? amountGive : Bad<decimal>(bad, AmountGiveField);

        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var field in ReferenceIdFields)
        {
            if (!root.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            if (TryReferenceId(value, out var id))
            {
                ids[field] = id;
            }
            else
            {
                bad.Add(field);
            }
        }

        return bad.Count == 0
            ? new PricingInput(give, receive, amount, ids)
            : throw new PricingException(InvalidRequest, [.. bad.Select(field => FieldPrefix + field)]);
    }

    private static string? StringOf(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static T Bad<T>(List<string> bad, string field)
    {
        bad.Add(field);
        return default!;
    }

    // A JSON string of at most 16 integer digits and 2 decimals, above zero; a JSON number never
    // gets here (D-007).
    private static bool TryAmount(string? text, out decimal amount)
    {
        amount = 0m;
        return text is not null
            && AmountPattern().IsMatch(text)
            && decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount)
            && amount > 0m;
    }

    // A JSON integer from 1 to 2147483647; TryGetInt32 refuses a fraction or an exponent (1.0, 1e0).
    private static bool TryReferenceId(JsonElement value, out int id)
    {
        id = 0;
        return value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out id)
            && id > 0;
    }

    [GeneratedRegex(@"\A(0|[1-9][0-9]{0,15})(\.[0-9]{1,2})?\z", RegexOptions.CultureInvariant)]
    private static partial Regex AmountPattern();
}
