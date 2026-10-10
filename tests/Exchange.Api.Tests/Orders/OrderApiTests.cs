using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Exchange.Api.Settings;
using Exchange.Api.Tests.Auth;
using Exchange.Api.Tests.Data;
using Exchange.Api.Tests.Rates;
using Exchange.Api.Tests.Settings;
using Npgsql;
using Shouldly;

namespace Exchange.Api.Tests.Orders;

/// <summary>
/// Spec 1.5 and 1.10, the client's orders: <c>POST /api/orders</c> prices on the server (INV-4),
/// checks the limits and the detail fields, and stores the order with its creation row; the list
/// and the single order show only the caller's own, masked; another client's order is 404
/// (INV-3). Mids are the fake 1 USD = 0.9 EUR = 90 RUB.
/// </summary>
public sealed class OrderApiTests(PostgresFixture database)
{
    private const long ClientId = 7_100_001;
    private const long OtherClientId = 7_100_002;
    private const string Iban = "ME25505000012345678951";

    [Fact]
    public async Task An_order_is_created_with_its_rate_snapshot_and_masked_details()
    {
        await using var app = await AppAsync();

        var (status, order) = await app.PostAsync(ClientId, WireOrder());

        status.ShouldBe(HttpStatusCode.Created);
        order.GetProperty("status").GetString().ShouldBe("PENDING");
        order.GetProperty("give").GetString().ShouldBe("USD_CASH");
        order.GetProperty("receive").GetString().ShouldBe("EUR_WIRE");
        order.GetProperty("amount_give").GetString().ShouldBe("100.00");
        order.GetProperty("amount_receive").GetString().ShouldBe("90.00");
        order.GetProperty("base_rate").GetString().ShouldBe("0.900000000000");
        order.GetProperty("effective_rate").GetString().ShouldBe("0.900000000000");
        order.GetProperty("provider").GetString().ShouldBe("ecb");
        order.GetProperty("closed_at").ValueKind.ShouldBe(JsonValueKind.Null);
        order.GetProperty("markup_percent").GetRawText().ShouldBe(
            """{"city":"0.0000","bank_give":"0.0000","bank_receive":"0.0000","network_give":"0.0000","network_receive":"0.0000","loyalty_discount":"0.0000"}""");
        order.GetProperty("details").GetRawText().ShouldBe(
            """{"city_id":null,"bank_give_id":null,"bank_receive_id":null,"network_give_id":null,"network_receive_id":null,"usdt_transfer_time":null,"iban":"ME25...8951","recipient_name":"Ivan Petrov","wallet_address":null,"payment_purpose":"Invoice 7"}""");
    }

    [Fact]
    public async Task The_order_and_its_creation_row_are_stored_and_its_pii_is_ciphertext()
    {
        await using var app = await AppAsync();

        var (_, order) = await app.PostAsync(ClientId, WireOrder());

        var id = order.GetProperty("id").GetInt32();
        (await app.ScalarAsync($"SELECT count(*) FROM order_status_history WHERE order_id = {id} AND from_status IS NULL AND to_status = 'PENDING' AND actor = 'system'")).ShouldBe(1L);
        var storedIban = (byte[])(await app.ScalarAsync($"SELECT iban FROM orders WHERE id = {id}"))!;
        storedIban.ShouldNotBeEmpty();
        Encoding.ASCII.GetString(storedIban).ShouldNotContain("ME25"); // INV-13: the column holds the cipher's token
    }

    [Fact]
    public async Task A_usdt_receive_order_masks_the_wallet()
    {
        await using var app = await AppAsync();

        var (status, order) = await app.PostAsync(ClientId, new Dictionary<string, object?>
        {
            ["give"] = "EUR_CASH",
            ["receive"] = "USDT",
            ["amount_give"] = "500",
            ["network_receive_id"] = 1,
            ["wallet_address"] = "  TQn9Y2khEsLJW1ChVWFMSMeRDow5KcbLSE ",
        });

        status.ShouldBe(HttpStatusCode.Created);
        order.GetProperty("details").GetProperty("wallet_address").GetString().ShouldBe("TQn9...bLSE");
        order.GetProperty("details").GetProperty("network_receive_id").GetInt32().ShouldBe(1);
    }

    // --- Limits (spec 1.5) ---------------------------------------------------------------------------

    [Theory]
    [InlineData("USD_CASH", "EUR_WIRE", "99.99", false)]
    [InlineData("USD_CASH", "EUR_WIRE", "100", true)]
    [InlineData("USD_CASH", "EUR_WIRE", "10000", true)]
    [InlineData("USD_CASH", "EUR_WIRE", "10000.01", false)]
    [InlineData("RUB_WIRE", "EUR_WIRE", "9999.99", false)]
    [InlineData("RUB_WIRE", "EUR_WIRE", "10000", true)]
    [InlineData("RUB_WIRE", "EUR_WIRE", "1000000", true)]
    [InlineData("RUB_WIRE", "EUR_WIRE", "1000000.01", false)]
    public async Task The_amount_must_lie_within_its_give_currency_limits(string give, string receive, string amount, bool accepted)
    {
        await using var app = await AppAsync();
        var body = new Dictionary<string, object?> { ["give"] = give, ["receive"] = receive, ["amount_give"] = amount, ["iban"] = Iban, ["recipient_name"] = "Ivan" };
        if (give == "RUB_WIRE")
        {
            body["bank_give_id"] = 1;
        }

        using var response = await app.SendAsync(ClientId, HttpMethod.Post, "/api/orders", body);

        if (accepted)
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
        }
        else
        {
            await RateApiTests.ShouldBe422Async(response, "amount_out_of_range", ["body.amount_give"]);
            (await app.ScalarAsync("SELECT count(*) FROM orders")).ShouldBe(0L);
        }
    }

    // --- Detail fields and the direction --------------------------------------------------------------

    [Theory]
    [InlineData("iban", "ME25505000012345678952", "body.iban")] // the check digits fail
    [InlineData("iban", "5", "body.iban")] // not a string
    [InlineData("recipient_name", "5", "body.recipient_name")] // a number is not a name, though "5" would be
    [InlineData("recipient_name", "Ivan\u0000", "body.recipient_name")]
    [InlineData("payment_purpose", "\u202eInvoice", "body.payment_purpose")]
    public async Task A_bad_detail_field_is_422_naming_it_and_nothing_is_stored(string field, string value, string named)
    {
        await using var app = await AppAsync();
        var body = WireOrder();
        body[field] = value == "5" ? 5 : value;

        using var response = await app.SendAsync(ClientId, HttpMethod.Post, "/api/orders", body);

        await RateApiTests.ShouldBe422Async(response, "invalid_request", [named]);
        (await app.ScalarAsync("SELECT count(*) FROM orders")).ShouldBe(0L);
    }

    [Fact]
    public async Task Bad_priced_and_detail_fields_are_listed_together()
    {
        await using var app = await AppAsync();
        var body = WireOrder();
        body["amount_give"] = 100; // a JSON number
        body["iban"] = "nope";
        body["usdt_transfer_time"] = "later";

        using var response = await app.SendAsync(ClientId, HttpMethod.Post, "/api/orders", body);

        await RateApiTests.ShouldBe422Async(response, "invalid_request", ["body.amount_give", "body.usdt_transfer_time", "body.iban"]);
    }

    [Fact]
    public async Task A_missing_required_detail_and_an_unused_one_are_listed()
    {
        await using var app = await AppAsync();
        var body = new Dictionary<string, object?> { ["give"] = "USD_CASH", ["receive"] = "EUR_CASH", ["amount_give"] = "100", ["payment_purpose"] = "x" };

        using var response = await app.SendAsync(ClientId, HttpMethod.Post, "/api/orders", body);

        await RateApiTests.ShouldBe422Async(response, "invalid_request", ["body.city_id", "body.payment_purpose"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task A_null_or_empty_optional_detail_counts_as_absent(string? purpose)
    {
        await using var app = await AppAsync();
        var body = WireOrder();
        body["payment_purpose"] = purpose;

        var (status, order) = await app.PostAsync(ClientId, body);

        status.ShouldBe(HttpStatusCode.Created);
        order.GetProperty("details").GetProperty("payment_purpose").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Without_a_fresh_rate_the_order_is_503_and_nothing_is_stored()
    {
        await using var app = await AppAsync(ecbWorks: false);

        using var response = await app.SendAsync(ClientId, HttpMethod.Post, "/api/orders", WireOrder());

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await app.ScalarAsync("SELECT count(*) FROM orders")).ShouldBe(0L);
    }

    // --- Reading: own orders only (INV-3) ---------------------------------------------------------------

    [Fact]
    public async Task The_list_is_the_callers_own_orders_newest_first()
    {
        await using var app = await AppAsync();
        var (_, first) = await app.PostAsync(ClientId, WireOrder());
        await app.PostAsync(OtherClientId, WireOrder());
        var (_, second) = await app.PostAsync(ClientId, WireOrder());

        using var response = await app.SendAsync(ClientId, HttpMethod.Get, "/api/orders", null);
        using var list = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        list.RootElement.EnumerateArray().Select(o => o.GetProperty("id").GetInt32())
            .ShouldBe([second.GetProperty("id").GetInt32(), first.GetProperty("id").GetInt32()]);
    }

    [Fact]
    public async Task An_own_order_is_read_and_another_clients_order_is_404()
    {
        await using var app = await AppAsync();
        var (_, mine) = await app.PostAsync(ClientId, WireOrder());
        var (_, theirs) = await app.PostAsync(OtherClientId, WireOrder());

        using var own = await app.SendAsync(ClientId, HttpMethod.Get, "/api/orders/" + mine.GetProperty("id").GetInt32(), null);
        using var other = await app.SendAsync(ClientId, HttpMethod.Get, "/api/orders/" + theirs.GetProperty("id").GetInt32(), null);

        own.StatusCode.ShouldBe(HttpStatusCode.OK);
        await ShouldBeNotFoundAsync(other);
    }

    [Theory]
    [InlineData("999")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("1.5")]
    [InlineData("99999999999")]
    public async Task An_order_id_that_names_no_order_is_404(string id)
    {
        await using var app = await AppAsync();
        await app.PostAsync(ClientId, WireOrder());

        using var response = await app.SendAsync(ClientId, HttpMethod.Get, "/api/orders/" + id, null);

        await ShouldBeNotFoundAsync(response);
    }

    internal static async Task ShouldBeNotFoundAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBe("""{"error":"not_found"}""");
    }

    internal static Dictionary<string, object?> WireOrder() => new()
    {
        ["give"] = "USD_CASH",
        ["receive"] = "EUR_WIRE",
        ["amount_give"] = "100",
        ["iban"] = "ME25 5050 0001 2345 6789 51",
        ["recipient_name"] = "  Ivan   Petrov ",
        ["payment_purpose"] = "Invoice 7",
    };

    private async Task<OrderApp> AppAsync(bool ecbWorks = true)
    {
        var environment = TestSettings.Production();
        environment["DATABASE_URL"] = await database.CreateMigratedDatabaseUrlAsync();
        return new OrderApp(environment, ecbWorks);
    }

    /// <summary>An app under test with fake rate sources, signing clients in by Telegram id.</summary>
    internal sealed class OrderApp : IAsyncDisposable
    {
        private readonly TestAppFactory _factory;
        private readonly HttpClient _client;
        private readonly string _connectionString;
        private readonly Dictionary<long, string> _tokens = [];

        public OrderApp(Dictionary<string, string?> environment, bool ecbWorks = true, Action<Microsoft.Extensions.DependencyInjection.IServiceCollection>? services = null)
        {
            _connectionString = DatabaseUrl.Convert(environment["DATABASE_URL"]!).ConnectionString!.Reveal();
            _factory = new TestAppFactory(environment, services: collection =>
            {
                FakeSources.Replace(ecbWorks)(collection);
                services?.Invoke(collection);
            });
            _client = _factory.CreateClient();
        }

        public TestAppFactory Factory => _factory;

        public async Task<string> TokenAsync(long telegramId)
        {
            if (!_tokens.TryGetValue(telegramId, out var token))
            {
                token = await TelegramAuth.SignInAsync(_client, TelegramAuth.InitData(TestSettings.BotToken, telegramId, "Client", "client_" + telegramId, DateTimeOffset.UtcNow.AddSeconds(-5)));
                _tokens[telegramId] = token;
            }

            return token;
        }

        public async Task<HttpResponseMessage> SendAsync(long telegramId, HttpMethod method, string path, object? body)
        {
            using var request = new HttpRequestMessage(method, path);
            if (body is not null)
            {
                request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            }

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(telegramId));
            return await _client.SendAsync(request, TestContext.Current.CancellationToken);
        }

        public async Task<(HttpStatusCode Status, JsonElement Order)> PostAsync(long telegramId, object body)
        {
            using var response = await SendAsync(telegramId, HttpMethod.Post, "/api/orders", body);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            return (response.StatusCode, document.RootElement.Clone());
        }

        public async Task<object?> ScalarAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = new NpgsqlCommand(sql, connection);
            return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await _factory.DisposeAsync();
        }
    }
}
