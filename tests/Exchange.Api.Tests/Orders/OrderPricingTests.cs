using System.Globalization;
using System.Net;
using System.Text.Json;
using Exchange.Api.Data;
using Exchange.Api.Tests.Data;
using Exchange.Api.Tests.Rates;
using Exchange.Api.Tests.Settings;
using Exchange.Domain.Directions;
using Exchange.Domain.Rates;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Exchange.Api.Tests.Orders;

/// <summary>
/// INV-4: an order stores the rate the server computes for its stored inputs, every markup
/// component and every detail it was given; a client-sent rate or amount changes nothing. The rows
/// use seeded reference rows with non-zero markups (spec 1.2) and the fake mids 1 USD = 0.9 EUR = 90 RUB.
/// </summary>
public sealed class OrderPricingTests(PostgresFixture database)
{
    private const long ClientId = 7_300_001;
    private const string Iban = "ME25505000012345678951";

    private static readonly Dictionary<Currency, decimal> Mids = new(FakeSources.EcbMids.Concat(FakeSources.CbrMids));

    public static TheoryData<string> Rows() => ["city", "banks", "network_give", "network_receive"];

    [Theory]
    [MemberData(nameof(Rows))]
    public async Task An_order_stores_the_server_computed_rate_every_markup_and_every_detail(string row)
    {
        var (body, give, receive, amount, markups, provider) = Case(row);
        await using var app = await AppAsync();

        var (status, order) = await app.PostAsync(ClientId, body);

        status.ShouldBe(HttpStatusCode.Created, order.GetRawText());
        var expected = RateEngine.Quote(give, receive, amount, Mids, markups, 0m);
        Text(order, "base_rate").ShouldBe(Plain(expected.BaseRate));
        Text(order, "effective_rate").ShouldBe(Plain(expected.EffectiveRate));
        Text(order, "amount_receive").ShouldBe(Plain(expected.AmountReceive));
        Text(order, "provider").ShouldBe(provider);
        var stored = order.GetProperty("markup_percent");
        Text(stored, "city").ShouldBe(Percent(markups.City));
        Text(stored, "bank_give").ShouldBe(Percent(markups.BankGive));
        Text(stored, "bank_receive").ShouldBe(Percent(markups.BankReceive));
        Text(stored, "network_give").ShouldBe(Percent(markups.NetworkGive));
        Text(stored, "network_receive").ShouldBe(Percent(markups.NetworkReceive));
        var details = order.GetProperty("details");
        foreach (var id in new[] { "city_id", "bank_give_id", "bank_receive_id", "network_give_id", "network_receive_id" })
        {
            var sent = body.TryGetValue(id, out var value) ? (int?)value : null;
            (details.GetProperty(id).ValueKind == JsonValueKind.Null ? (int?)null : details.GetProperty(id).GetInt32()).ShouldBe(sent, id);
        }

        (details.GetProperty("usdt_transfer_time").ValueKind == JsonValueKind.Null ? null : details.GetProperty("usdt_transfer_time").GetString())
            .ShouldBe(body.TryGetValue("usdt_transfer_time", out var time) ? (string?)time : null);
        DateTimeOffset.Parse(Text(order, "rate_taken_at"), CultureInfo.InvariantCulture).ShouldBeInRange(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow);
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public async Task The_created_order_and_the_same_order_read_back_are_identical(string row)
    {
        await using var app = await AppAsync();
        var (_, created) = await app.PostAsync(ClientId, Case(row).Body);

        using var response = await app.SendAsync(ClientId, HttpMethod.Get, "/api/orders/" + created.GetProperty("id").GetInt32(), null);
        using var read = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        read.RootElement.GetRawText().ShouldBe(created.GetRawText());
    }

    [Fact]
    public async Task A_client_sent_rate_or_amount_changes_nothing()
    {
        await using var app = await AppAsync();
        var (body, give, receive, amount, markups, _) = Case("banks");
        body["amount_receive"] = "1000000";
        body["rate"] = "999";
        body["effective_rate"] = "5";
        body["base_rate"] = "5";
        body["user_id"] = 1;

        var (_, order) = await app.PostAsync(ClientId, body);

        var expected = RateEngine.Quote(give, receive, amount, Mids, markups, 0m);
        Text(order, "effective_rate").ShouldBe(Plain(expected.EffectiveRate));
        Text(order, "base_rate").ShouldBe(Plain(expected.BaseRate));
        Text(order, "amount_receive").ShouldBe(Plain(expected.AmountReceive));
        (await app.ScalarAsync($"SELECT effective_rate::text FROM orders WHERE id = {order.GetProperty("id").GetInt32()}")).ShouldBe(Plain(expected.EffectiveRate));
    }

    [Fact]
    public async Task An_order_whose_history_row_cannot_be_saved_is_not_left_behind()
    {
        await using var app = await AppAsync(services => services.ConfigureDbContext<ExchangeDbContext>(options => options.AddInterceptors(new FailingHistoryInterceptor())));

        using var response = await app.SendAsync(ClientId, HttpMethod.Post, "/api/orders", Case("city").Body);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        (await app.ScalarAsync("SELECT count(*) FROM orders")).ShouldBe(0L);
    }

    // Each row: the request, its direction and amount, the seeded markups it names, and the provider.
    private static (Dictionary<string, object?> Body, GiveCode Give, ReceiveCode Receive, decimal Amount, Markups Markups, string Provider) Case(string row) => row switch
    {
        "city" => (new() { ["give"] = "USD_CASH", ["receive"] = "EUR_CASH", ["amount_give"] = "500", ["city_id"] = 2 },
            GiveCode.UsdCash, ReceiveCode.EurCash, 500m, new Markups(City: 0.5m), "ecb"), // Budva
        "banks" => (new() { ["give"] = "RUB_WIRE", ["receive"] = "EUR_ATM", ["amount_give"] = "50000", ["bank_give_id"] = 2, ["bank_receive_id"] = 3, ["iban"] = Iban, ["recipient_name"] = "Ivan" },
            GiveCode.RubWire, ReceiveCode.EurAtm, 50_000m, new Markups(BankGive: 0.5m), "cbr+ecb"), // TINKOFF 0.5, LOVCEN 0.0: the two differ
        "network_give" => (new() { ["give"] = "USDT", ["receive"] = "EUR_WIRE", ["amount_give"] = "1000", ["network_give_id"] = 3, ["usdt_transfer_time"] = "at_meeting", ["iban"] = Iban, ["recipient_name"] = "Ivan" },
            GiveCode.Usdt, ReceiveCode.EurWire, 1000m, new Markups(NetworkGive: 1.0m), "ecb"), // ERC20
        _ => (new() { ["give"] = "EUR_CASH", ["receive"] = "USDT", ["amount_give"] = "500", ["network_receive_id"] = 2, ["wallet_address"] = "TQn9Y2khEsLJW1ChVWFMSMeRDow5KcbLSE" },
            GiveCode.EurCash, ReceiveCode.Usdt, 500m, new Markups(NetworkReceive: 0.4m), "ecb"), // TON
    };

    private static string Text(JsonElement element, string name) => element.GetProperty(name).GetString()!;

    private static string Plain(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Percent(decimal value) => value.ToString("0.0000", CultureInfo.InvariantCulture);

    private async Task<OrderApiTests.OrderApp> AppAsync(Action<IServiceCollection>? services = null)
    {
        var environment = TestSettings.Production();
        environment["DATABASE_URL"] = await database.CreateMigratedDatabaseUrlAsync();
        return new OrderApiTests.OrderApp(environment, services: services);
    }

    /// <summary>Fails any save that adds a history row, as a database fault between the two inserts would.</summary>
    private sealed class FailingHistoryInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            eventData.Context!.ChangeTracker.Entries<OrderStatusChange>().Any(entry => entry.State == EntityState.Added)
                ? throw new InvalidOperationException("history insert failed")
                : base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}
