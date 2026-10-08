namespace Exchange.Domain.Directions;

/// <summary>The request property names of the extra fields of spec 1.1 and 1.10.</summary>
public static class DetailFields
{
    /// <summary><c>city_id</c>.</summary>
    public const string CityId = "city_id";

    /// <summary><c>bank_give_id</c>.</summary>
    public const string BankGiveId = "bank_give_id";

    /// <summary><c>bank_receive_id</c>.</summary>
    public const string BankReceiveId = "bank_receive_id";

    /// <summary><c>network_give_id</c>.</summary>
    public const string NetworkGiveId = "network_give_id";

    /// <summary><c>network_receive_id</c>.</summary>
    public const string NetworkReceiveId = "network_receive_id";

    /// <summary><c>usdt_transfer_time</c>.</summary>
    public const string UsdtTransferTime = "usdt_transfer_time";

    /// <summary><c>iban</c>.</summary>
    public const string Iban = "iban";

    /// <summary><c>recipient_name</c>.</summary>
    public const string RecipientName = "recipient_name";

    /// <summary><c>wallet_address</c>.</summary>
    public const string WalletAddress = "wallet_address";

    /// <summary><c>payment_purpose</c>.</summary>
    public const string PaymentPurpose = "payment_purpose";

    /// <summary>The ids whose reference rows carry a markup: all a quote accepts (spec 1.3, 1.10).</summary>
    public static readonly IReadOnlySet<string> Priced = new HashSet<string>(StringComparer.Ordinal)
    {
        CityId, BankGiveId, BankReceiveId, NetworkGiveId, NetworkReceiveId,
    };

    /// <summary>Every extra field an order accepts.</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(Priced, StringComparer.Ordinal)
    {
        UsdtTransferTime, Iban, RecipientName, WalletAddress, PaymentPurpose,
    };
}

/// <summary>What a request is for: a quote takes only the priced fields; an order takes them all.</summary>
public enum Purpose
{
    /// <summary><c>POST /api/rate</c>.</summary>
    Quote,

    /// <summary><c>POST /api/orders</c>.</summary>
    Order,
}

/// <summary>One valid pair of section 1.1.</summary>
/// <param name="Give">What the client gives.</param>
/// <param name="Receive">What the client receives.</param>
public sealed record Direction(GiveCode Give, ReceiveCode Receive);

/// <summary>The fields a direction requires and those it may carry, for one purpose.</summary>
/// <param name="Required">Fields that must be present.</param>
/// <param name="Optional">Fields that may be present.</param>
public sealed record DirectionFields(IReadOnlySet<string> Required, IReadOnlySet<string> Optional);

/// <summary>Why a direction or its fields were refused; names fields, never their values.</summary>
/// <param name="SameBase">Both sides resolve to the same base currency (spec 1.1).</param>
/// <param name="Missing">Required fields that are absent, sorted.</param>
/// <param name="Unexpected">Accepted fields the direction does not use, sorted.</param>
public sealed record DirectionProblem(bool SameBase, IReadOnlyList<string> Missing, IReadOnlyList<string> Unexpected);

/// <summary>
/// The exchange directions of spec 1.1 and the extra fields each side requires - the one table
/// the API enforces and <c>GET /api/directions</c> publishes (INV-6).
/// </summary>
public static class Directions
{
    private static readonly Dictionary<GiveCode, string[]> GiveRequired = new()
    {
        [GiveCode.RubWire] = [DetailFields.BankGiveId],
        [GiveCode.Usdt] = [DetailFields.NetworkGiveId, DetailFields.UsdtTransferTime],
        [GiveCode.UsdCash] = [],
        [GiveCode.EurCash] = [],
    };

    private static readonly Dictionary<ReceiveCode, string[]> ReceiveRequired = new()
    {
        [ReceiveCode.EurCash] = [DetailFields.CityId],
        [ReceiveCode.EurAtm] = [DetailFields.BankReceiveId, DetailFields.Iban, DetailFields.RecipientName],
        [ReceiveCode.EurWire] = [DetailFields.Iban, DetailFields.RecipientName],
        [ReceiveCode.EurInvoice] = [DetailFields.Iban, DetailFields.RecipientName],
        [ReceiveCode.Usdt] = [DetailFields.NetworkReceiveId, DetailFields.WalletAddress],
    };

    private static readonly Dictionary<ReceiveCode, string[]> ReceiveOptional = new()
    {
        [ReceiveCode.EurWire] = [DetailFields.PaymentPurpose],
        [ReceiveCode.EurInvoice] = [DetailFields.PaymentPurpose],
    };

    /// <summary>Every valid direction, give then receive in declaration order.</summary>
    public static IReadOnlyList<Direction> All { get; } =
        [.. Enum.GetValues<GiveCode>().SelectMany(give => Enum.GetValues<ReceiveCode>()
            .Where(receive => BaseOf(give) != BaseOf(receive))
            .Select(receive => new Direction(give, receive)))];

    /// <summary>The base currency of a give side (spec 1.3); USDT counts as USD (NR-1).</summary>
    public static Currency BaseOf(GiveCode give) => give switch
    {
        GiveCode.RubWire => Currency.Rub,
        GiveCode.UsdCash => Currency.Usd,
        GiveCode.EurCash => Currency.Eur,
        GiveCode.Usdt => Currency.Usd, // NR-1: the USDT peg is a domain claim awaiting review
        _ => throw new ArgumentOutOfRangeException(nameof(give), give, "unknown give code"),
    };

    /// <summary>The base currency of a receive side (spec 1.3); USDT counts as USD (NR-1).</summary>
    public static Currency BaseOf(ReceiveCode receive) => receive switch
    {
        ReceiveCode.EurCash or ReceiveCode.EurAtm or ReceiveCode.EurWire or ReceiveCode.EurInvoice => Currency.Eur,
        ReceiveCode.Usdt => Currency.Usd, // NR-1
        _ => throw new ArgumentOutOfRangeException(nameof(receive), receive, "unknown receive code"),
    };

    /// <summary>The fields a direction requires and allows for a purpose.</summary>
    public static DirectionFields FieldsFor(GiveCode give, ReceiveCode receive, Purpose purpose)
    {
        var required = new HashSet<string>(GiveRequired[give].Concat(ReceiveRequired[receive]), StringComparer.Ordinal);
        var optional = new HashSet<string>(ReceiveOptional.GetValueOrDefault(receive, []), StringComparer.Ordinal);
        if (purpose == Purpose.Quote)
        {
            required.IntersectWith(DetailFields.Priced);
            optional.IntersectWith(DetailFields.Priced);
        }

        return new DirectionFields(required, optional);
    }

    /// <summary>
    /// Checks a request's direction and the extra fields it carries (<paramref name="provided"/>:
    /// the names present with a value). Null when acceptable (INV-6, spec 1.10 request rules).
    /// </summary>
    public static DirectionProblem? Check(GiveCode give, ReceiveCode receive, IEnumerable<string> provided, Purpose purpose)
    {
        ArgumentNullException.ThrowIfNull(provided);
        if (BaseOf(give) == BaseOf(receive))
        {
            return new DirectionProblem(SameBase: true, Missing: [], Unexpected: []);
        }

        var fields = FieldsFor(give, receive, purpose);
        var accepted = purpose == Purpose.Quote ? DetailFields.Priced : DetailFields.All;
        var present = new HashSet<string>(provided, StringComparer.Ordinal);
        var missing = fields.Required.Where(name => !present.Contains(name)).Order(StringComparer.Ordinal).ToList();
        var unexpected = present.Where(name => accepted.Contains(name) && !fields.Required.Contains(name) && !fields.Optional.Contains(name))
            .Order(StringComparer.Ordinal).ToList();
        return missing.Count == 0 && unexpected.Count == 0 ? null : new DirectionProblem(false, missing, unexpected);
    }
}
