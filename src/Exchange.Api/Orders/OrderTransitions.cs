using Exchange.Api.Data;
using Exchange.Domain.Orders;
using Microsoft.EntityFrameworkCore;

namespace Exchange.Api.Orders;

/// <summary>What a status change came to.</summary>
public enum TransitionOutcome
{
    /// <summary>The status changed and its history row was written.</summary>
    Changed,

    /// <summary>No such order, or not the client's own.</summary>
    NotFound,

    /// <summary>The move is not one the actor may make from the order's current status; nothing was written.</summary>
    NotAllowed,
}

/// <summary>The result of a status change: the outcome, the order as it now stands, and its previous status.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Order">The order, when it exists and is visible to the actor.</param>
/// <param name="From">Its status before the change.</param>
public sealed record TransitionResult(TransitionOutcome Outcome, Order? Order, OrderStatus? From);

/// <summary>
/// The one transition function of spec 1.5: every status change - the client's cancel, the
/// operator's buttons, the admin later - goes through it. The order is read under a row lock
/// (<c>SELECT ... FOR UPDATE</c>), the move is checked against <see cref="StatusMachine"/> for the
/// actor, and the new status, <c>closed_at</c> and the history row are written in one transaction;
/// a refused move writes nothing (INV-9).
/// </summary>
public sealed partial class OrderTransitions(ExchangeDbContext db, TimeProvider time, ILogger<OrderTransitions> logger)
{
    /// <summary>Moves order <paramref name="orderId"/> to <paramref name="to"/> for <paramref name="actor"/>.</summary>
    /// <param name="orderId">The order.</param>
    /// <param name="to">The status wanted.</param>
    /// <param name="actor">Who asks.</param>
    /// <param name="actorTelegramId">The Telegram id of the person asking, recorded in the history.</param>
    /// <param name="ownerUserId">For a client: the user the order must belong to; null for the operator.</param>
    /// <param name="cancellationToken">The request's cancellation.</param>
    public async Task<TransitionResult> ChangeAsync(int orderId, OrderStatus to, Actor actor, long? actorTelegramId, int? ownerUserId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var order = await db.Orders.FromSql($"SELECT * FROM orders WHERE id = {orderId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (order is null || (ownerUserId is { } owner && order.UserId != owner))
        {
            var actorName = OrderCodes.Of(actor);
            LogNotFound(logger, orderId, actorName);
            return new TransitionResult(TransitionOutcome.NotFound, null, null);
        }

        var from = order.Status;
        var (fromCode, toCode, actorCode) = (OrderCodes.Of(from), OrderCodes.Of(to), OrderCodes.Of(actor));
        if (!StatusMachine.MayMove(actor, from, to))
        {
            LogRefused(logger, order.Id, fromCode, toCode, actorCode);
            return new TransitionResult(TransitionOutcome.NotAllowed, order, from);
        }

        var now = time.GetUtcNow();
        order.Status = to;
        if (StatusMachine.IsClosed(to))
        {
            order.ClosedAt = OrderEndpoints.ToMicroseconds(now);
        }

        db.OrderStatusHistory.Add(new OrderStatusChange { OrderId = order.Id, FromStatus = from, ToStatus = to, Actor = actor, ActorTelegramId = actorTelegramId });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        LogChanged(logger, order.Id, fromCode, toCode, actorCode);
        return new TransitionResult(TransitionOutcome.Changed, order, from);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Order {OrderId} status {From} -> {To} by {Actor}")]
    private static partial void LogChanged(ILogger logger, int orderId, string from, string to, string actor);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Order {OrderId} not found for a status change by {Actor}")]
    private static partial void LogNotFound(ILogger logger, int orderId, string actor);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Order {OrderId} status change {From} -> {To} by {Actor} refused")]
    private static partial void LogRefused(ILogger logger, int orderId, string from, string to, string actor);
}
