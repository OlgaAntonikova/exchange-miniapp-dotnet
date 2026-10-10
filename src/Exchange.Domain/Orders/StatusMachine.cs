namespace Exchange.Domain.Orders;

/// <summary>
/// The status machine of spec 1.5 (INV-9): <c>PENDING -> CONFIRMED -> PROCESSING -> COMPLETED</c>,
/// and <c>CANCELLED</c> from <c>PENDING</c>, <c>CONFIRMED</c> or <c>PROCESSING</c>. The client may
/// only cancel a <c>PENDING</c> order; every other change is the operator's (or the admin's).
/// </summary>
public static class StatusMachine
{
    // Forward first, then cancelling: the order the operator's buttons show them in.
    private static readonly Dictionary<OrderStatus, OrderStatus[]> Moves = new()
    {
        [OrderStatus.Pending] = [OrderStatus.Confirmed, OrderStatus.Cancelled],
        [OrderStatus.Confirmed] = [OrderStatus.Processing, OrderStatus.Cancelled],
        [OrderStatus.Processing] = [OrderStatus.Completed, OrderStatus.Cancelled],
        [OrderStatus.Completed] = [],
        [OrderStatus.Cancelled] = [],
    };

    /// <summary>The statuses <paramref name="from"/> may move to, in the order the operator's buttons show them.</summary>
    public static IReadOnlyList<OrderStatus> Next(OrderStatus from) => Moves[from];

    /// <summary>Whether <paramref name="from"/> may move to <paramref name="to"/> at all.</summary>
    public static bool CanMove(OrderStatus from, OrderStatus to) => Moves[from].Contains(to);

    /// <summary>Whether <paramref name="actor"/> may make that move.</summary>
    public static bool MayMove(Actor actor, OrderStatus from, OrderStatus to) => actor switch
    {
        Actor.Operator or Actor.Admin => CanMove(from, to),
        Actor.Client => from == OrderStatus.Pending && to == OrderStatus.Cancelled,
        _ => false, // the system creates an order; it changes none
    };

    /// <summary>Whether the status ends the order, which sets its <c>closed_at</c>.</summary>
    public static bool IsClosed(OrderStatus status) => status is OrderStatus.Completed or OrderStatus.Cancelled;
}
