using Exchange.Api.Auth;
using Exchange.Api.Bot;
using Exchange.Api.Data;
using Exchange.Api.Locale;
using Exchange.Api.Pricing;
using Exchange.Api.Settings;
using Exchange.Domain.Directions;
using Exchange.Domain.Money;
using Exchange.Domain.Orders;
using Microsoft.EntityFrameworkCore;

namespace Exchange.Api.Orders;

/// <summary>
/// The signed-in client's orders (spec 1.5, 1.10). Every route needs a valid token (INV-3), and
/// another client's order answers 404 exactly like a missing one. An order is priced by the server
/// exactly as a quote is (INV-4) and stored with its rate snapshot and its first history row in one
/// transaction; responses mask the IBAN and the wallet address.
/// </summary>
public static partial class OrderEndpoints
{
    /// <summary>The newest orders a client's list shows.</summary>
    public const int ListMaxOrders = 50;

    private const string LoggerCategory = "Exchange.Api.Orders";
    private const string OrdersRoute = "/api/orders";

    /// <summary>An order's markups in percent: the stored components only (spec 1.10).</summary>
    /// <param name="City">The city's markup.</param>
    /// <param name="BankGive">The sender bank's markup.</param>
    /// <param name="BankReceive">The ATM bank's markup.</param>
    /// <param name="NetworkGive">The give-side network fee.</param>
    /// <param name="NetworkReceive">The receive-side network fee.</param>
    /// <param name="LoyaltyDiscount">The loyalty discount.</param>
    public sealed record OrderMarkupOut(decimal City, decimal BankGive, decimal BankReceive, decimal NetworkGive, decimal NetworkReceive, decimal LoyaltyDiscount);

    /// <summary>An order's details, IBAN and wallet masked.</summary>
    /// <param name="CityId">The city.</param>
    /// <param name="BankGiveId">The sender bank.</param>
    /// <param name="BankReceiveId">The ATM bank.</param>
    /// <param name="NetworkGiveId">The give network.</param>
    /// <param name="NetworkReceiveId">The receive network.</param>
    /// <param name="UsdtTransferTime">When USDT is sent.</param>
    /// <param name="Iban">The IBAN, masked.</param>
    /// <param name="RecipientName">The recipient, as stored.</param>
    /// <param name="WalletAddress">The wallet, masked.</param>
    /// <param name="PaymentPurpose">The purpose, as stored.</param>
    public sealed record OrderDetailsOut(int? CityId, int? BankGiveId, int? BankReceiveId, int? NetworkGiveId, int? NetworkReceiveId, string? UsdtTransferTime, string? Iban, string? RecipientName, string? WalletAddress, string? PaymentPurpose);

    /// <summary>An order, as spec 1.10 defines it.</summary>
    /// <param name="Id">Identity.</param>
    /// <param name="Status">The status.</param>
    /// <param name="Give">The give code.</param>
    /// <param name="Receive">The receive code.</param>
    /// <param name="AmountGive">The amount given.</param>
    /// <param name="AmountReceive">The amount received.</param>
    /// <param name="BaseRate">The mid-market rate.</param>
    /// <param name="EffectiveRate">The rate after markups.</param>
    /// <param name="MarkupPercent">The stored markups.</param>
    /// <param name="Provider">The rate sources.</param>
    /// <param name="RateTakenAt">The older of their snapshot times.</param>
    /// <param name="CreatedAt">When it was placed.</param>
    /// <param name="ClosedAt">When it was completed or cancelled.</param>
    /// <param name="Details">The details.</param>
    public sealed record OrderOut(
        int Id,
        string Status,
        string Give,
        string Receive,
        decimal AmountGive,
        decimal AmountReceive,
        decimal BaseRate,
        decimal EffectiveRate,
        OrderMarkupOut MarkupPercent,
        string Provider,
        DateTimeOffset RateTakenAt,
        DateTimeOffset CreatedAt,
        DateTimeOffset? ClosedAt,
        OrderDetailsOut Details);

    /// <summary>Maps <c>POST /api/orders</c>, <c>GET /api/orders</c>, <c>GET /api/orders/{order_id}</c> and <c>POST /api/orders/{order_id}/cancel</c>.</summary>
    public static IEndpointRouteBuilder MapOrderEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(OrdersRoute, async (HttpContext http, ExchangeDbContext db, Pricer pricer, AppSettings settings, LocaleTexts texts, OrderNotifications notifications) =>
        {
            var (user, refusal) = await AuthEndpoints.CallerAsync(http, db).ConfigureAwait(false);
            if (user is null)
            {
                return refusal!;
            }

            try
            {
                using var body = await PricingRefusal.ReadBodyAsync(http.Request, http.RequestAborted).ConfigureAwait(false);
                var input = OrderRequest.Parse(body.RootElement);
                var order = await CreateAsync(db, pricer, settings, user, input, http.RequestAborted).ConfigureAwait(false);
                var give = Codes.Of(order.Give);
                var receive = Codes.Of(order.Receive);
                var logger = Logger(http);
                LogCreated(logger, order.Id, give, receive, order.RateProvider);

                // Queued after the commit and never failing the order: the operator's message.
                await notifications.OrderCreatedAsync(order, user).ConfigureAwait(false);
                return Results.Created(OrdersRoute + "/" + order.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), Out(order));
            }
            catch (PricingException refused)
            {
                return PricingRefusal.Result(http, texts, refused, "Order");
            }
        }).RequireAuthorization();

        app.MapGet(OrdersRoute, async (HttpContext http, ExchangeDbContext db) =>
        {
            var (user, refusal) = await AuthEndpoints.CallerAsync(http, db).ConfigureAwait(false);
            if (user is null)
            {
                return refusal!;
            }

            var mine = await db.Orders.AsNoTracking().Where(order => order.UserId == user.Id)
                .OrderByDescending(order => order.Id).Take(ListMaxOrders)
                .ToListAsync(http.RequestAborted).ConfigureAwait(false);
            return Results.Ok(mine.Select(Out).ToList());
        }).RequireAuthorization();

        app.MapGet(OrdersRoute + "/{order_id}", async (string order_id, HttpContext http, ExchangeDbContext db) =>
        {
            var (user, refusal) = await AuthEndpoints.CallerAsync(http, db).ConfigureAwait(false);
            if (user is null)
            {
                return refusal!;
            }

            var order = TryOrderId(order_id) is { } id
                ? await db.Orders.AsNoTracking().SingleOrDefaultAsync(o => o.Id == id && o.UserId == user.Id, http.RequestAborted).ConfigureAwait(false)
                : null;
            if (order is null)
            {
                var logger = Logger(http);
                LogNotFound(logger);
                return NotFound();
            }

            return Results.Ok(Out(order));
        }).RequireAuthorization();

        app.MapPost(OrdersRoute + "/{order_id}/cancel", async (string order_id, HttpContext http, ExchangeDbContext db, OrderTransitions transitions, OrderNotifications notifications) =>
        {
            var (user, refusal) = await AuthEndpoints.CallerAsync(http, db).ConfigureAwait(false);
            if (user is null)
            {
                return refusal!;
            }

            if (TryOrderId(order_id) is not { } id)
            {
                var logger = Logger(http);
                LogNotFound(logger);
                return NotFound();
            }

            // A missing or someone else's order is logged by the transition function.
            var result = await transitions.ChangeAsync(id, OrderStatus.Cancelled, Actor.Client, user.TelegramId, user.Id, http.RequestAborted).ConfigureAwait(false);
            if (result.Outcome == TransitionOutcome.Changed)
            {
                notifications.StatusChanged(result.Order!, user);
            }

            return result.Outcome switch
            {
                TransitionOutcome.Changed => Results.Ok(Out(result.Order!)),
                TransitionOutcome.NotAllowed => NotCancellable(),
                _ => NotFound(),
            };
        }).RequireAuthorization();

        return app;
    }

    /// <summary>The 409 of INV-21: cancelling an order that is not <c>PENDING</c>.</summary>
    public static IResult NotCancellable() => Results.Json(new Dictionary<string, string> { ["error"] = "not_cancellable" }, statusCode: StatusCodes.Status409Conflict);

    /// <summary>
    /// An order id from the path: plain ASCII digits that fit a 32-bit integer (no sign, no space).
    /// Anything else names no order, which is the 404 of INV-21 rather than a framework 400 with a
    /// body the spec does not list; 0 is parsed and simply finds no order.
    /// </summary>
    public static int? TryOrderId(string text) =>
        int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) ? id : null;

    /// <summary>The 404 of INV-21: an order that does not exist or belongs to someone else.</summary>
    public static IResult NotFound() => Results.Json(new Dictionary<string, string> { ["error"] = "not_found" }, statusCode: StatusCodes.Status404NotFound);

    /// <summary>The response shape of an order.</summary>
    public static OrderOut Out(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);
        return new OrderOut(
            order.Id,
            OrderCodes.Of(order.Status),
            Codes.Of(order.Give),
            Codes.Of(order.Receive),
            order.AmountGive,
            order.AmountReceive,
            order.BaseRate,
            order.EffectiveRate,
            new OrderMarkupOut(order.CityMarkupPercent, order.BankGiveMarkupPercent, order.BankReceiveMarkupPercent, order.NetworkGiveFeePercent, order.NetworkReceiveFeePercent, order.LoyaltyDiscountPercent),
            order.RateProvider,
            order.RateSnapshotAt,
            order.CreatedAt,
            order.ClosedAt,
            new OrderDetailsOut(
                order.CityId,
                order.BankGiveId,
                order.BankReceiveId,
                order.NetworkGiveId,
                order.NetworkReceiveId,
                order.UsdtTransferTime is { } time ? Codes.Of(time) : null,
                DetailFieldRules.Mask(order.Iban),
                order.RecipientName,
                DetailFieldRules.Mask(order.WalletAddress),
                order.PaymentPurpose));
    }

    // The limits first (spec 1.5, inclusive, in the give currency), then the one pricing path a quote
    // takes; the order and its creation row are saved together.
    private static async Task<Order> CreateAsync(ExchangeDbContext db, Pricer pricer, AppSettings settings, AppUser user, OrderInput input, CancellationToken cancellationToken)
    {
        var pricing = input.Pricing;
        var limit = settings.OrderLimits[Codes.Of(pricing.Give)];
        if (pricing.AmountGive < limit.Min || pricing.AmountGive > limit.Max)
        {
            throw new PricingException(Pricer.AmountOutOfRange, [PricingRequest.FieldPrefix + "amount_give"]);
        }

        var priced = await pricer.PriceAsync(pricing, input.Provided, Purpose.Order, cancellationToken).ConfigureAwait(false);
        var quote = priced.Quote;
        var ids = pricing.ReferenceIds;
        var order = new Order
        {
            UserId = user.Id,
            Status = OrderStatus.Pending,
            Give = quote.Give,
            Receive = quote.Receive,
            AmountGive = Quantize.Amount(quote.AmountGive),
            AmountReceive = quote.AmountReceive,
            RateProvider = priced.Sourced.Provider,
            RateSnapshotAt = ToMicroseconds(priced.Sourced.TakenAt),
            BaseRate = quote.BaseRate,
            CityMarkupPercent = quote.Markups.City,
            BankGiveMarkupPercent = quote.Markups.BankGive,
            BankReceiveMarkupPercent = quote.Markups.BankReceive,
            NetworkGiveFeePercent = quote.Markups.NetworkGive,
            NetworkReceiveFeePercent = quote.Markups.NetworkReceive,
            LoyaltyDiscountPercent = quote.LoyaltyDiscountPercent,
            EffectiveRate = quote.EffectiveRate,
            CityId = ids.GetValueOrDefault(DetailFields.CityId) is var city and > 0 ? city : null,
            BankGiveId = ids.GetValueOrDefault(DetailFields.BankGiveId) is var bankGive and > 0 ? bankGive : null,
            BankReceiveId = ids.GetValueOrDefault(DetailFields.BankReceiveId) is var bankReceive and > 0 ? bankReceive : null,
            NetworkGiveId = ids.GetValueOrDefault(DetailFields.NetworkGiveId) is var networkGive and > 0 ? networkGive : null,
            NetworkReceiveId = ids.GetValueOrDefault(DetailFields.NetworkReceiveId) is var networkReceive and > 0 ? networkReceive : null,
            UsdtTransferTime = input.Details.UsdtTransferTime,
            Iban = input.Details.Iban,
            RecipientName = input.Details.RecipientName,
            WalletAddress = input.Details.WalletAddress,
            PaymentPurpose = input.Details.PaymentPurpose,
        };
        // One transaction: the history row needs the order's id, and neither may exist without the other.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        db.Orders.Add(order);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        db.OrderStatusHistory.Add(new OrderStatusChange { OrderId = order.Id, FromStatus = null, ToStatus = OrderStatus.Pending, Actor = Actor.System });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return order;
    }

    // timestamptz keeps microseconds; the clock gives 100-nanosecond ticks. Cut to the column's
    // precision before saving, so the created order and the same order read back say the same.
    internal static DateTimeOffset ToMicroseconds(DateTimeOffset moment) =>
        new(moment.Ticks - (moment.Ticks % TimeSpan.TicksPerMicrosecond), moment.Offset);

    private static ILogger Logger(HttpContext http) => http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(LoggerCategory);

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Order {OrderId} created: {Give} -> {Receive} from {Provider}")]
    private static partial void LogCreated(ILogger logger, int orderId, string give, string receive, string provider);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Order not found for the caller")]
    private static partial void LogNotFound(ILogger logger);
}
