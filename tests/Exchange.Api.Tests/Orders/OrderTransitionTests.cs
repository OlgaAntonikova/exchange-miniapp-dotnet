using System.Net;
using System.Text.Json;
using Exchange.Api.Orders;
using Exchange.Api.Tests.Data;
using Exchange.Api.Tests.Settings;
using Exchange.Domain.Orders;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Exchange.Api.Tests.Orders;

/// <summary>
/// Spec 1.5 and INV-9, through the one transition function: the client cancels only their own
/// <c>PENDING</c> order; every change writes its history row and closing sets <c>closed_at</c>; a
/// refused move writes nothing; concurrent changes are serialized by the row lock. The 409 and 404
/// are the bodies of INV-21.
/// </summary>
public sealed class OrderTransitionTests(PostgresFixture database)
{
    private const long ClientId = 7_400_001;
    private const long OtherClientId = 7_400_002;
    private const long OperatorId = 7_400_100;

    [Fact]
    public async Task A_client_cancels_their_pending_order()
    {
        await using var app = await AppAsync();
        var id = await PlaceAsync(app);

        using var response = await app.SendAsync(ClientId, HttpMethod.Post, $"/api/orders/{id}/cancel", null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var order = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        order.RootElement.GetProperty("status").GetString().ShouldBe("CANCELLED");
        order.RootElement.GetProperty("closed_at").ValueKind.ShouldBe(JsonValueKind.String);
        (await app.ScalarAsync($"SELECT count(*) FROM order_status_history WHERE order_id = {id} AND from_status = 'PENDING' AND to_status = 'CANCELLED' AND actor = 'client' AND actor_telegram_id = {ClientId}")).ShouldBe(1L);
        (await app.ScalarAsync($"SELECT closed_at IS NOT NULL FROM orders WHERE id = {id}")).ShouldBe(true);
    }

    [Theory]
    [InlineData("CONFIRMED")]
    [InlineData("PROCESSING")]
    [InlineData("COMPLETED")]
    [InlineData("CANCELLED")]
    public async Task Only_a_pending_order_can_be_cancelled_by_the_client_and_a_refusal_writes_nothing(string status)
    {
        await using var app = await AppAsync();
        var id = await PlaceAsync(app);
        await app.ScalarAsync($"UPDATE orders SET status = '{status}' WHERE id = {id}");

        using var response = await app.SendAsync(ClientId, HttpMethod.Post, $"/api/orders/{id}/cancel", null);

        await ShouldBeNotCancellableAsync(response);
        (await app.ScalarAsync($"SELECT status FROM orders WHERE id = {id}")).ShouldBe(status);
        (await app.ScalarAsync($"SELECT count(*) FROM order_status_history WHERE order_id = {id}")).ShouldBe(1L); // the creation row only
    }

    [Theory]
    [InlineData("other")]
    [InlineData("999")]
    [InlineData("abc")]
    public async Task Cancelling_an_order_that_is_not_the_callers_is_404_and_changes_nothing(string which)
    {
        await using var app = await AppAsync();
        var theirs = await PlaceAsync(app, OtherClientId);
        var path = which == "other" ? $"/api/orders/{theirs}/cancel" : $"/api/orders/{which}/cancel";

        using var response = await app.SendAsync(ClientId, HttpMethod.Post, path, null);

        await OrderApiTests.ShouldBeNotFoundAsync(response);
        (await app.ScalarAsync($"SELECT status FROM orders WHERE id = {theirs}")).ShouldBe("PENDING");
    }

    [Theory]
    [InlineData("other", "Order {OrderId} not found for a status change by {Actor}")]
    [InlineData("abc", "Order not found for the caller")]
    public async Task A_cancel_404_is_logged_at_warning(string which, string template)
    {
        var logs = new Logging.CapturingSink();
        var environment = TestSettings.Production();
        environment["DATABASE_URL"] = await database.CreateMigratedDatabaseUrlAsync();
        await using var app = new OrderApiTests.OrderApp(environment, logs: logs);
        var theirs = await PlaceAsync(app, OtherClientId);

        using var response = await app.SendAsync(ClientId, HttpMethod.Post, which == "other" ? $"/api/orders/{theirs}/cancel" : "/api/orders/abc/cancel", null);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        logs.Events.ShouldContain(e => e.Level == Serilog.Events.LogEventLevel.Warning && e.MessageTemplate.Text == template);
    }

    [Fact]
    public async Task The_closed_at_a_cancel_answers_is_the_one_stored()
    {
        await using var app = await AppAsync();
        var id = await PlaceAsync(app);

        using var cancel = await app.SendAsync(ClientId, HttpMethod.Post, $"/api/orders/{id}/cancel", null);
        using var read = await app.SendAsync(ClientId, HttpMethod.Get, $"/api/orders/{id}", null);

        (await read.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBe(await cancel.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Concurrent_cancels_change_the_order_once()
    {
        const int Callers = 8;
        await using var app = await AppAsync();
        var id = await PlaceAsync(app);
        await app.TokenAsync(ClientId);

        var responses = await Task.WhenAll(Enumerable.Range(0, Callers).Select(_ => app.SendAsync(ClientId, HttpMethod.Post, $"/api/orders/{id}/cancel", null)));

        responses.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        responses.Count(r => r.StatusCode == HttpStatusCode.Conflict).ShouldBe(Callers - 1);
        (await app.ScalarAsync($"SELECT count(*) FROM order_status_history WHERE order_id = {id} AND to_status = 'CANCELLED'")).ShouldBe(1L);
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task The_operator_moves_an_order_through_to_completed_and_only_completing_closes_it()
    {
        await using var app = await AppAsync();
        var id = await PlaceAsync(app);

        foreach (var (to, closed) in new[] { (OrderStatus.Confirmed, false), (OrderStatus.Processing, false), (OrderStatus.Completed, true) })
        {
            var result = await ChangeAsync(app, id, to, Actor.Operator, OperatorId);
            result.Outcome.ShouldBe(TransitionOutcome.Changed);
            result.Order!.Status.ShouldBe(to);
            (result.Order.ClosedAt is not null).ShouldBe(closed);
        }

        (await app.ScalarAsync($"SELECT string_agg(coalesce(from_status, '-') || '>' || to_status || ':' || actor, ',' ORDER BY id) FROM order_status_history WHERE order_id = {id}"))
            .ShouldBe("->PENDING:system,PENDING>CONFIRMED:operator,CONFIRMED>PROCESSING:operator,PROCESSING>COMPLETED:operator");
        (await app.ScalarAsync($"SELECT count(*) FROM order_status_history WHERE order_id = {id} AND actor = 'operator' AND actor_telegram_id = {OperatorId}")).ShouldBe(3L);
    }

    [Fact]
    public async Task The_operator_cannot_skip_a_status()
    {
        await using var app = await AppAsync();
        var id = await PlaceAsync(app);

        var result = await ChangeAsync(app, id, OrderStatus.Completed, Actor.Operator, OperatorId);

        result.Outcome.ShouldBe(TransitionOutcome.NotAllowed);
        (await app.ScalarAsync($"SELECT status FROM orders WHERE id = {id}")).ShouldBe("PENDING");
        (await app.ScalarAsync($"SELECT count(*) FROM order_status_history WHERE order_id = {id}")).ShouldBe(1L);
    }

    [Fact]
    public async Task The_operator_reaches_any_clients_order()
    {
        await using var app = await AppAsync();
        var id = await PlaceAsync(app, OtherClientId);

        (await ChangeAsync(app, id, OrderStatus.Confirmed, Actor.Operator, OperatorId)).Outcome.ShouldBe(TransitionOutcome.Changed);
        (await ChangeAsync(app, 999, OrderStatus.Confirmed, Actor.Operator, OperatorId)).Outcome.ShouldBe(TransitionOutcome.NotFound);
    }

    internal static async Task ShouldBeNotCancellableAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBe("""{"error":"not_cancellable"}""");
    }

    internal static async Task<int> PlaceAsync(OrderApiTests.OrderApp app, long clientId = ClientId)
    {
        var (status, order) = await app.PostAsync(clientId, OrderApiTests.WireOrder());
        status.ShouldBe(HttpStatusCode.Created);
        return order.GetProperty("id").GetInt32();
    }

    private static async Task<TransitionResult> ChangeAsync(OrderApiTests.OrderApp app, int id, OrderStatus to, Actor actor, long telegramId)
    {
        await using var scope = app.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OrderTransitions>().ChangeAsync(id, to, actor, telegramId, null, TestContext.Current.CancellationToken);
    }

    private async Task<OrderApiTests.OrderApp> AppAsync()
    {
        var environment = TestSettings.Production();
        environment["DATABASE_URL"] = await database.CreateMigratedDatabaseUrlAsync();
        return new OrderApiTests.OrderApp(environment);
    }
}
