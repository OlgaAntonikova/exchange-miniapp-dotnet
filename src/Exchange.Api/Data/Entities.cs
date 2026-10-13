using Exchange.Domain.Directions;
using Exchange.Domain.Orders;

namespace Exchange.Api.Data;

/// <summary>A city where cash is handed over (spec 1.2).</summary>
public sealed class City
{
    /// <summary>Identity.</summary>
    public int Id { get; set; }

    /// <summary>The city's name; unique.</summary>
    public required string Name { get; set; }

    /// <summary>ISO 3166-1 alpha-2 country code.</summary>
    public required string Country { get; set; }

    /// <summary>Markup in percent, at the percentage scale.</summary>
    public decimal MarkupPercent { get; set; }

    /// <summary>Inactive rows are invisible to clients and refused in requests.</summary>
    public bool IsActive { get; set; } = true;
}

/// <summary>A bank that sends a transfer or pays out at its ATMs (spec 1.2).</summary>
public sealed class Bank
{
    /// <summary>Identity.</summary>
    public int Id { get; set; }

    /// <summary>The bank's code; unique together with the channel.</summary>
    public required string Code { get; set; }

    /// <summary>What the bank does for the client.</summary>
    public BankChannel Channel { get; set; }

    /// <summary>ISO 4217 currency code.</summary>
    public required string Currency { get; set; }

    /// <summary>ISO 3166-1 alpha-2 country code.</summary>
    public required string Country { get; set; }

    /// <summary>Markup in percent.</summary>
    public decimal MarkupPercent { get; set; }

    /// <summary>Inactive rows are invisible to clients and refused in requests.</summary>
    public bool IsActive { get; set; } = true;
}

/// <summary>A USDT network (spec 1.2).</summary>
public sealed class UsdtNetwork
{
    /// <summary>Identity.</summary>
    public int Id { get; set; }

    /// <summary>The network's code; unique.</summary>
    public required string Code { get; set; }

    /// <summary>Fee in percent.</summary>
    public decimal FeePercent { get; set; }

    /// <summary>Inactive rows are invisible to clients and refused in requests.</summary>
    public bool IsActive { get; set; } = true;
}

/// <summary>A client, known by their Telegram id (spec 1.10).</summary>
public sealed class AppUser
{
    /// <summary>Identity.</summary>
    public int Id { get; set; }

    /// <summary>Telegram's user id; unique.</summary>
    public long TelegramId { get; set; }

    /// <summary>Telegram username, if any.</summary>
    public string? Username { get; set; }

    /// <summary>Telegram first name, if any.</summary>
    public string? FirstName { get; set; }

    /// <summary>Loyalty points, 0 to 10 (spec 1.6).</summary>
    public int LoyaltyPoints { get; set; }

    /// <summary>When the user was first seen.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>An exchange request with the rate snapshot it was priced from (spec 1.5).</summary>
public sealed class Order
{
    /// <summary>Identity.</summary>
    public int Id { get; set; }

    /// <summary>The client.</summary>
    public int UserId { get; set; }

    /// <summary>The current status.</summary>
    public OrderStatus Status { get; set; }

    /// <summary>What the client gives.</summary>
    public GiveCode Give { get; set; }

    /// <summary>What the client receives.</summary>
    public ReceiveCode Receive { get; set; }

    /// <summary>The amount given, in the give currency.</summary>
    public decimal AmountGive { get; set; }

    /// <summary>The amount received, computed by the server.</summary>
    public decimal AmountReceive { get; set; }

    /// <summary>The providers the rate came from (<c>ecb</c>, <c>cbr</c>, <c>cbr+ecb</c>).</summary>
    public required string RateProvider { get; set; }

    /// <summary>The older of the snapshots the rate came from.</summary>
    public DateTimeOffset RateSnapshotAt { get; set; }

    /// <summary>The mid-market rate before markups.</summary>
    public decimal BaseRate { get; set; }

    /// <summary>The city's markup, or zero.</summary>
    public decimal CityMarkupPercent { get; set; }

    /// <summary>The sender bank's markup, or zero.</summary>
    public decimal BankGiveMarkupPercent { get; set; }

    /// <summary>The paying bank's markup, or zero.</summary>
    public decimal BankReceiveMarkupPercent { get; set; }

    /// <summary>The give network's fee, or zero.</summary>
    public decimal NetworkGiveFeePercent { get; set; }

    /// <summary>The receive network's fee, or zero.</summary>
    public decimal NetworkReceiveFeePercent { get; set; }

    /// <summary>The loyalty discount applied, in percent.</summary>
    public decimal LoyaltyDiscountPercent { get; set; }

    /// <summary>The rate after markups.</summary>
    public decimal EffectiveRate { get; set; }

    /// <summary>The city, for <c>EUR_CASH</c>.</summary>
    public int? CityId { get; set; }

    /// <summary>The sender bank, for <c>RUB_WIRE</c>.</summary>
    public int? BankGiveId { get; set; }

    /// <summary>The paying bank, for <c>EUR_ATM</c>.</summary>
    public int? BankReceiveId { get; set; }

    /// <summary>The give network, for a USDT give.</summary>
    public int? NetworkGiveId { get; set; }

    /// <summary>The receive network, for a USDT receive.</summary>
    public int? NetworkReceiveId { get; set; }

    /// <summary>When USDT is sent, for a USDT give.</summary>
    public UsdtTransferTime? UsdtTransferTime { get; set; }

    /// <summary>PII; encrypted at rest (INV-13).</summary>
    public string? Iban { get; set; }

    /// <summary>PII; encrypted at rest.</summary>
    public string? RecipientName { get; set; }

    /// <summary>PII; encrypted at rest.</summary>
    public string? WalletAddress { get; set; }

    /// <summary>PII; encrypted at rest.</summary>
    public string? PaymentPurpose { get; set; }

    /// <summary>When the order was placed.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When it reached COMPLETED or CANCELLED.</summary>
    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>When its PII was purged (spec 1.5; the purge job is <c>L-4</c>).</summary>
    public DateTimeOffset? PiiPurgedAt { get; set; }
}

/// <summary>One status change of an order, its creation included (spec 1.5).</summary>
public sealed class OrderStatusChange
{
    /// <summary>Identity.</summary>
    public int Id { get; set; }

    /// <summary>The order.</summary>
    public int OrderId { get; set; }

    /// <summary>The status before; null for the creation.</summary>
    public OrderStatus? FromStatus { get; set; }

    /// <summary>The status after.</summary>
    public OrderStatus ToStatus { get; set; }

    /// <summary>Who made the change.</summary>
    public Actor Actor { get; set; }

    /// <summary>The Telegram id of the person who made it, when a person did.</summary>
    public long? ActorTelegramId { get; set; }

    /// <summary>When.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}
