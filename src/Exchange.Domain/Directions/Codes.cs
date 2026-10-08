namespace Exchange.Domain.Directions;

/// <summary>What the client gives (spec 1.1).</summary>
public enum GiveCode
{
    /// <summary><c>RUB_WIRE</c>: roubles by bank transfer.</summary>
    RubWire,

    /// <summary><c>USD_CASH</c>: dollars in cash.</summary>
    UsdCash,

    /// <summary><c>EUR_CASH</c>: euros in cash.</summary>
    EurCash,

    /// <summary><c>USDT</c>: the USDT stablecoin.</summary>
    Usdt,
}

/// <summary>What the client receives (spec 1.1).</summary>
public enum ReceiveCode
{
    /// <summary><c>EUR_CASH</c>: euros in cash, in a city.</summary>
    EurCash,

    /// <summary><c>EUR_ATM</c>: euros withdrawn at a bank's ATM.</summary>
    EurAtm,

    /// <summary><c>EUR_WIRE</c>: euros by bank transfer.</summary>
    EurWire,

    /// <summary><c>EUR_INVOICE</c>: euros paid against an invoice.</summary>
    EurInvoice,

    /// <summary><c>USDT</c>: the USDT stablecoin.</summary>
    Usdt,
}

/// <summary>A base currency of the rate engine (spec 1.3).</summary>
public enum Currency
{
    /// <summary>US dollar.</summary>
    Usd,

    /// <summary>Euro.</summary>
    Eur,

    /// <summary>Russian rouble.</summary>
    Rub,
}

/// <summary>When a client giving USDT transfers it (spec 1.1).</summary>
public enum UsdtTransferTime
{
    /// <summary><c>now</c>.</summary>
    Now,

    /// <summary><c>at_meeting</c>.</summary>
    AtMeeting,
}

/// <summary>
/// The wire codes of spec 1.1 and 1.10 - the strings in JSON and in the database - in one place.
/// Parsing is exact: no other spelling or case is a code.
/// </summary>
public static class Codes
{
    private static readonly Dictionary<GiveCode, string> GiveNames = new()
    {
        [GiveCode.RubWire] = "RUB_WIRE",
        [GiveCode.UsdCash] = "USD_CASH",
        [GiveCode.EurCash] = "EUR_CASH",
        [GiveCode.Usdt] = "USDT",
    };

    private static readonly Dictionary<ReceiveCode, string> ReceiveNames = new()
    {
        [ReceiveCode.EurCash] = "EUR_CASH",
        [ReceiveCode.EurAtm] = "EUR_ATM",
        [ReceiveCode.EurWire] = "EUR_WIRE",
        [ReceiveCode.EurInvoice] = "EUR_INVOICE",
        [ReceiveCode.Usdt] = "USDT",
    };

    private static readonly Dictionary<UsdtTransferTime, string> TransferTimeNames = new()
    {
        [UsdtTransferTime.Now] = "now",
        [UsdtTransferTime.AtMeeting] = "at_meeting",
    };

    private static readonly Dictionary<string, GiveCode> GiveByName = Invert(GiveNames);
    private static readonly Dictionary<string, ReceiveCode> ReceiveByName = Invert(ReceiveNames);
    private static readonly Dictionary<string, UsdtTransferTime> TransferTimeByName = Invert(TransferTimeNames);

    /// <summary>The wire code of a give side.</summary>
    public static string Of(GiveCode code) => GiveNames[code];

    /// <summary>The wire code of a receive side.</summary>
    public static string Of(ReceiveCode code) => ReceiveNames[code];

    /// <summary>The wire value of a transfer time.</summary>
    public static string Of(UsdtTransferTime time) => TransferTimeNames[time];

    /// <summary>Parses a give code exactly.</summary>
    public static bool TryParseGive(string? text, out GiveCode code) => GiveByName.TryGetValue(text ?? string.Empty, out code);

    /// <summary>Parses a receive code exactly.</summary>
    public static bool TryParseReceive(string? text, out ReceiveCode code) => ReceiveByName.TryGetValue(text ?? string.Empty, out code);

    /// <summary>Parses a transfer time exactly.</summary>
    public static bool TryParseTransferTime(string? text, out UsdtTransferTime time) =>
        TransferTimeByName.TryGetValue(text ?? string.Empty, out time);

    private static Dictionary<string, TEnum> Invert<TEnum>(Dictionary<TEnum, string> names)
        where TEnum : struct, Enum =>
        names.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);
}
