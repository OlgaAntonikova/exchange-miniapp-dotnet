namespace Exchange.Domain.Orders;

/// <summary>The status of an exchange request (spec 1.5).</summary>
public enum OrderStatus
{
    /// <summary><c>PENDING</c>: placed, waiting for the operator.</summary>
    Pending,

    /// <summary><c>CONFIRMED</c>: accepted by the operator.</summary>
    Confirmed,

    /// <summary><c>PROCESSING</c>: the deal is under way.</summary>
    Processing,

    /// <summary><c>COMPLETED</c>: done.</summary>
    Completed,

    /// <summary><c>CANCELLED</c>: called off.</summary>
    Cancelled,
}

/// <summary>Who changed an order's status (spec 1.5); <c>system</c> is the order's creation.</summary>
public enum Actor
{
    /// <summary><c>client</c>.</summary>
    Client,

    /// <summary><c>operator</c>.</summary>
    Operator,

    /// <summary><c>admin</c>.</summary>
    Admin,

    /// <summary><c>system</c>.</summary>
    System,
}

/// <summary>How a bank serves the client (spec 1.1, 1.2).</summary>
public enum BankChannel
{
    /// <summary><c>WIRE</c>: a sender bank for a transfer.</summary>
    Wire,

    /// <summary><c>ATM</c>: a bank whose ATMs pay out cash.</summary>
    Atm,
}

/// <summary>The stored and wire spellings of <see cref="OrderStatus"/>, <see cref="Actor"/> and <see cref="BankChannel"/>.</summary>
public static class OrderCodes
{
    private static readonly Dictionary<OrderStatus, string> StatusNames = new()
    {
        [OrderStatus.Pending] = "PENDING",
        [OrderStatus.Confirmed] = "CONFIRMED",
        [OrderStatus.Processing] = "PROCESSING",
        [OrderStatus.Completed] = "COMPLETED",
        [OrderStatus.Cancelled] = "CANCELLED",
    };

    private static readonly Dictionary<Actor, string> ActorNames = new()
    {
        [Actor.Client] = "client",
        [Actor.Operator] = "operator",
        [Actor.Admin] = "admin",
        [Actor.System] = "system",
    };

    private static readonly Dictionary<BankChannel, string> ChannelNames = new()
    {
        [BankChannel.Wire] = "WIRE",
        [BankChannel.Atm] = "ATM",
    };

    /// <summary>Every stored status spelling, for the database's CHECK constraint.</summary>
    public static IReadOnlyCollection<string> AllStatuses => StatusNames.Values;

    /// <summary>Every stored actor spelling.</summary>
    public static IReadOnlyCollection<string> AllActors => ActorNames.Values;

    /// <summary>Every stored channel spelling.</summary>
    public static IReadOnlyCollection<string> AllChannels => ChannelNames.Values;

    /// <summary>The spelling of a status.</summary>
    public static string Of(OrderStatus status) => StatusNames[status];

    /// <summary>The spelling of an actor.</summary>
    public static string Of(Actor actor) => ActorNames[actor];

    /// <summary>The spelling of a channel.</summary>
    public static string Of(BankChannel channel) => ChannelNames[channel];

    /// <summary>Parses a status exactly.</summary>
    public static OrderStatus ParseStatus(string text) => Parse(StatusNames, text, "order status");

    /// <summary>Parses an actor exactly.</summary>
    public static Actor ParseActor(string text) => Parse(ActorNames, text, "actor");

    /// <summary>Parses a channel exactly.</summary>
    public static BankChannel ParseChannel(string text) => Parse(ChannelNames, text, "bank channel");

    private static TEnum Parse<TEnum>(Dictionary<TEnum, string> names, string text, string what)
        where TEnum : struct, Enum
    {
        foreach (var (value, name) in names)
        {
            if (string.Equals(name, text, StringComparison.Ordinal))
            {
                return value;
            }
        }

        throw new FormatException($"unknown {what}");
    }
}
