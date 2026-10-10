using Exchange.Domain.Orders;
using Shouldly;

namespace Exchange.Domain.Tests.Orders;

/// <summary>
/// INV-9, the transitions of spec 1.5: the allowed set is written out here by hand, and every one of
/// the 25 pairs is checked against it, so a pair the machine allows by mistake fails too.
/// </summary>
public sealed class StatusMachineTests
{
    private static readonly HashSet<(OrderStatus, OrderStatus)> Allowed =
    [
        (OrderStatus.Pending, OrderStatus.Confirmed),
        (OrderStatus.Confirmed, OrderStatus.Processing),
        (OrderStatus.Processing, OrderStatus.Completed),
        (OrderStatus.Pending, OrderStatus.Cancelled),
        (OrderStatus.Confirmed, OrderStatus.Cancelled),
        (OrderStatus.Processing, OrderStatus.Cancelled),
    ];

    public static TheoryData<OrderStatus, OrderStatus> EveryPair() =>
        [.. Enum.GetValues<OrderStatus>().SelectMany(from => Enum.GetValues<OrderStatus>().Select(to => (from, to)))];

    [Fact]
    public void There_are_twenty_five_pairs() => EveryPair().Count.ShouldBe(25);

    [Theory]
    [MemberData(nameof(EveryPair))]
    public void Only_the_transitions_of_spec_1_5_are_allowed(OrderStatus from, OrderStatus to) =>
        StatusMachine.CanMove(from, to).ShouldBe(Allowed.Contains((from, to)));

    [Theory]
    [MemberData(nameof(EveryPair))]
    public void The_next_statuses_are_exactly_the_allowed_moves(OrderStatus from, OrderStatus to) =>
        StatusMachine.Next(from).Contains(to).ShouldBe(Allowed.Contains((from, to)));

    [Fact]
    public void The_buttons_show_the_way_forward_before_cancelling()
    {
        StatusMachine.Next(OrderStatus.Pending).ShouldBe([OrderStatus.Confirmed, OrderStatus.Cancelled]);
        StatusMachine.Next(OrderStatus.Confirmed).ShouldBe([OrderStatus.Processing, OrderStatus.Cancelled]);
        StatusMachine.Next(OrderStatus.Processing).ShouldBe([OrderStatus.Completed, OrderStatus.Cancelled]);
        StatusMachine.Next(OrderStatus.Completed).ShouldBeEmpty();
        StatusMachine.Next(OrderStatus.Cancelled).ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(EveryPair))]
    public void The_operator_and_the_admin_may_make_every_allowed_move(OrderStatus from, OrderStatus to)
    {
        StatusMachine.MayMove(Actor.Operator, from, to).ShouldBe(Allowed.Contains((from, to)));
        StatusMachine.MayMove(Actor.Admin, from, to).ShouldBe(Allowed.Contains((from, to)));
    }

    [Theory]
    [MemberData(nameof(EveryPair))]
    public void The_client_may_only_cancel_a_pending_order(OrderStatus from, OrderStatus to) =>
        StatusMachine.MayMove(Actor.Client, from, to).ShouldBe(from == OrderStatus.Pending && to == OrderStatus.Cancelled);

    [Theory]
    [MemberData(nameof(EveryPair))]
    public void The_system_makes_no_move_it_only_creates(OrderStatus from, OrderStatus to) =>
        StatusMachine.MayMove(Actor.System, from, to).ShouldBeFalse();

    [Theory]
    [InlineData(OrderStatus.Completed, true)]
    [InlineData(OrderStatus.Cancelled, true)]
    [InlineData(OrderStatus.Pending, false)]
    [InlineData(OrderStatus.Confirmed, false)]
    [InlineData(OrderStatus.Processing, false)]
    public void Completed_and_cancelled_close_the_order(OrderStatus status, bool closed) =>
        StatusMachine.IsClosed(status).ShouldBe(closed);
}
