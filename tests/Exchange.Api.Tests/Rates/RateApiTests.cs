using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Exchange.Api.Rates;
using Exchange.Api.Settings;
using Exchange.Api.Tests.Auth;
using Exchange.Api.Tests.Data;
using Exchange.Api.Tests.Settings;
using Exchange.Domain.Directions;
using Npgsql;
using Shouldly;

namespace Exchange.Api.Tests.Rates;

/// <summary>
/// Spec 1.3 and 1.10, <c>POST /api/rate</c>: a live quote with its breakdown, priced by the server
/// from the seeded markups (spec 1.2) and fake mids of 1 USD = 0.9 EUR = 90 RUB. Every expected
/// value is computed by hand. Refusals are the 422 and 503 bodies of INV-21; a stale source
/// refuses exactly the directions that need it (INV-5).
/// </summary>
public sealed class RateApiTests(PostgresFixture database)
{
    private const string RatesUnavailableText = "\u041a\u0443\u0440\u0441 \u0441\u0435\u0439\u0447\u0430\u0441 \u043d\u0435\u0434\u043e\u0441\u0442\u0443\u043f\u0435\u043d. \u041f\u043e\u043f\u0440\u043e\u0431\u0443\u0439\u0442\u0435 \u0447\u0435\u0440\u0435\u0437 \u043d\u0435\u0441\u043a\u043e\u043b\u044c\u043a\u043e \u043c\u0438\u043d\u0443\u0442.";

    [Fact]
    public async Task Dollars_to_euro_cash_in_budva_carry_the_city_markup()
    {
        var (status, body) = await PostAsync("""{"give": "USD_CASH", "receive": "EUR_CASH", "amount_give": "100", "city_id": 2}""");

        status.ShouldBe(HttpStatusCode.OK);
        var quote = body.RootElement;
        quote.EnumerateObject().Select(p => p.Name).ShouldBe(
            ["give", "receive", "amount_give", "amount_receive", "base_rate", "effective_rate", "markup_percent", "provider", "rate_taken_at"], ignoreOrder: true);
        Text(quote, "give").ShouldBe("USD_CASH");
        Text(quote, "receive").ShouldBe("EUR_CASH");
        Text(quote, "amount_give").ShouldBe("100.00");
        Text(quote, "base_rate").ShouldBe("0.900000000000");
        Text(quote, "effective_rate").ShouldBe("0.895500000000"); // 0.9 * 0.995
        Text(quote, "amount_receive").ShouldBe("89.55");
        Text(quote, "provider").ShouldBe("ecb");
        quote.GetProperty("markup_percent").GetRawText().ShouldBe(
            """{"city":"0.5000","bank_give":"0.0000","bank_receive":"0.0000","network_give":"0.0000","network_receive":"0.0000","total":"0.5000","loyalty_discount":"0.0000","applied":"0.5000"}""");
        DateTimeOffset.Parse(Text(quote, "rate_taken_at"), System.Globalization.CultureInfo.InvariantCulture)
            .ShouldBeInRange(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
        Text(quote, "rate_taken_at").ShouldEndWith("+00:00");
    }

    [Fact]
    public async Task Roubles_to_an_atm_use_both_sources_and_both_banks()
    {
        var (status, body) = await PostAsync("""{"give": "RUB_WIRE", "receive": "EUR_ATM", "amount_give": "10000", "bank_give_id": 2, "bank_receive_id": 5}""");

        status.ShouldBe(HttpStatusCode.OK);
        Text(body.RootElement, "base_rate").ShouldBe("0.010000000000"); // 0.9 / 90
        Text(body.RootElement, "effective_rate").ShouldBe("0.009900000000"); // TINKOFF 0.5 + ERSTE 0.5
        Text(body.RootElement, "amount_receive").ShouldBe("99.00");
        Text(body.RootElement, "provider").ShouldBe("cbr+ecb");
    }

    [Fact]
    public async Task Usdt_to_euro_cash_carries_the_network_fee()
    {
        var (_, body) = await PostAsync("""{"give": "USDT", "receive": "EUR_CASH", "amount_give": "1000", "network_give_id": 1, "city_id": 1}""");

        Text(body.RootElement, "effective_rate").ShouldBe("0.897300000000"); // TRC20 0.3, Podgorica 0.0
        Text(body.RootElement, "amount_receive").ShouldBe("897.30");
        body.RootElement.GetProperty("markup_percent").GetProperty("network_give").GetString().ShouldBe("0.3000");
    }

    // Every direction of spec 1.1 with the first seeded row of each field it needs.
    public static TheoryData<string, string> EveryDirection() =>
        [.. Directions.All.Select(d => (Codes.Of(d.Give), Codes.Of(d.Receive)))];

    [Theory]
    [MemberData(nameof(EveryDirection))]
    public async Task Every_direction_quotes(string give, string receive)
    {
        var (status, body) = await PostAsync(QuoteJson(give, receive));

        status.ShouldBe(HttpStatusCode.OK, body.RootElement.GetRawText());
        decimal.Parse(Text(body.RootElement, "amount_receive"), System.Globalization.CultureInfo.InvariantCulture).ShouldBePositive();
    }

    [Fact]
    public async Task Properties_outside_the_contract_change_nothing()
    {
        var (_, plain) = await PostAsync("""{"give": "USD_CASH", "receive": "EUR_WIRE", "amount_give": "100"}""");
        var (_, padded) = await PostAsync("""{"give": "USD_CASH", "receive": "EUR_WIRE", "amount_give": "100", "amount_receive": "1000000", "rate": "999", "user_id": 1, "effective_rate": "5"}""");

        Text(padded.RootElement, "amount_receive").ShouldBe(Text(plain.RootElement, "amount_receive"));
        Text(padded.RootElement, "effective_rate").ShouldBe("0.900000000000");
    }

    // --- 422 ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("""{"give": "USD_CASH", "receive": "USDT", "amount_give": "100"}""", "invalid_direction", "body.give,body.receive")]
    [InlineData("""{"give": "EUR_CASH", "receive": "EUR_WIRE", "amount_give": "100"}""", "invalid_direction", "body.give,body.receive")]
    [InlineData("""{"give": "RUB_WIRE", "receive": "EUR_CASH", "amount_give": "10000"}""", "invalid_request", "body.bank_give_id,body.city_id")] // missing
    [InlineData("""{"give": "USD_CASH", "receive": "EUR_WIRE", "amount_give": "100", "city_id": 1, "network_give_id": 1}""", "invalid_request", "body.city_id,body.network_give_id")] // not used
    [InlineData("""{"give": "USD_CASH", "receive": "EUR_CASH", "amount_give": "100", "city_id": 1, "bank_give_id": 1}""", "invalid_request", "body.bank_give_id")]
    [InlineData("""{"give": "USD_CASH", "receive": "EUR_WIRE", "amount_give": 100}""", "invalid_request", "body.amount_give")] // a JSON number (INV-14)
    [InlineData("""{"give": "USD_CASH", "receive": "EUR_WIRE", "amount_give": "100.001"}""", "invalid_request", "body.amount_give")]
    [InlineData("""{"give": "USD_CASH", "receive": "EUR_CASH", "amount_give": "100", "city_id": "1"}""", "invalid_request", "body.city_id")]
    [InlineData("not json", "invalid_request", "body")]
    [InlineData("""{"give": "USD_CASH", "give": "EUR_CASH", "receive": "EUR_WIRE", "amount_give": "100"}""", "invalid_request", "body")] // which give?
    public async Task A_request_breaking_the_rules_is_422(string json, string error, string fields) =>
        await ShouldBe422Async(json, error, fields.Split(','));

    [Theory]
    [InlineData("""{"give": "USD_CASH", "receive": "EUR_CASH", "amount_give": "100", "city_id": 99}""", "body.city_id")] // no such city
    [InlineData("""{"give": "RUB_WIRE", "receive": "EUR_WIRE", "amount_give": "10000", "bank_give_id": 3}""", "body.bank_give_id")] // an ATM bank as the sender
    [InlineData("""{"give": "USD_CASH", "receive": "EUR_ATM", "amount_give": "100", "bank_receive_id": 1}""", "body.bank_receive_id")] // a RUB wire bank as the ATM
    [InlineData("""{"give": "USDT", "receive": "EUR_WIRE", "amount_give": "100", "network_give_id": 99}""", "body.network_give_id")]
    [InlineData("""{"give": "RUB_WIRE", "receive": "USDT", "amount_give": "10000", "bank_give_id": 3, "network_receive_id": 99}""", "body.bank_give_id,body.network_receive_id")] // every bad row
    public async Task A_reference_row_that_does_not_exist_or_does_not_fit_its_side_is_422(string json, string fields) =>
        await ShouldBe422Async(json, "invalid_request", fields.Split(','));

    [Theory]
    [InlineData("cities", 2, """{"give": "USD_CASH", "receive": "EUR_CASH", "amount_give": "100", "city_id": 2}""", "body.city_id")]
    [InlineData("banks", 2, """{"give": "RUB_WIRE", "receive": "EUR_WIRE", "amount_give": "10000", "bank_give_id": 2}""", "body.bank_give_id")]
    [InlineData("usdt_networks", 1, """{"give": "USDT", "receive": "EUR_WIRE", "amount_give": "100", "network_give_id": 1}""", "body.network_give_id")]
    public async Task An_inactive_reference_row_is_422(string table, int id, string json, string field)
    {
        var url = await database.CreateMigratedDatabaseUrlAsync();
        await using (var connection = new NpgsqlConnection(DatabaseUrl.Convert(url).ConnectionString!.Reveal()))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = new NpgsqlCommand($"UPDATE {table} SET is_active = false WHERE id = {id}", connection);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await ShouldBe422Async(json, "invalid_request", [field], url);
    }

    // A row that differs from what its side allows in one attribute only: each check stands alone.
    [Theory]
    [InlineData("INSERT INTO banks (code, channel, currency, country, markup_percent, is_active) VALUES ('X', 'ATM', 'RUB', 'RU', 0, true)", "RUB_WIRE", "EUR_WIRE", "bank_give_id")] // channel
    [InlineData("INSERT INTO banks (code, channel, currency, country, markup_percent, is_active) VALUES ('X', 'WIRE', 'EUR', 'RU', 0, true)", "RUB_WIRE", "EUR_WIRE", "bank_give_id")] // currency
    [InlineData("INSERT INTO banks (code, channel, currency, country, markup_percent, is_active) VALUES ('X', 'WIRE', 'RUB', 'ME', 0, true)", "RUB_WIRE", "EUR_WIRE", "bank_give_id")] // country
    [InlineData("INSERT INTO banks (code, channel, currency, country, markup_percent, is_active) VALUES ('X', 'WIRE', 'EUR', 'ME', 0, true)", "USD_CASH", "EUR_ATM", "bank_receive_id")] // channel
    [InlineData("INSERT INTO banks (code, channel, currency, country, markup_percent, is_active) VALUES ('X', 'ATM', 'RUB', 'ME', 0, true)", "USD_CASH", "EUR_ATM", "bank_receive_id")] // currency
    [InlineData("INSERT INTO banks (code, channel, currency, country, markup_percent, is_active) VALUES ('X', 'ATM', 'EUR', 'RU', 0, true)", "USD_CASH", "EUR_ATM", "bank_receive_id")] // country
    [InlineData("INSERT INTO cities (name, country, markup_percent, is_active) VALUES ('Belgrade', 'RS', 0, true)", "USD_CASH", "EUR_CASH", "city_id")] // not Montenegro
    public async Task A_row_off_its_side_by_one_attribute_is_422(string insert, string give, string receive, string field)
    {
        var url = await database.CreateMigratedDatabaseUrlAsync();
        int id;
        await using (var connection = new NpgsqlConnection(DatabaseUrl.Convert(url).ConnectionString!.Reveal()))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = new NpgsqlCommand(insert + " RETURNING id", connection);
            id = (int)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
        }

        var json = $$"""{"give": "{{give}}", "receive": "{{receive}}", "amount_give": "10000", "{{field}}": {{id}}}""";
        await ShouldBe422Async(json, "invalid_request", ["body." + field], url);
    }

    [Fact]
    public async Task A_body_over_the_cap_is_422_for_the_body() =>
        await ShouldBe422Async("{" + Quoted("give") + ": " + Quoted("USD_CASH") + ", " + Quoted("receive") + ": " + Quoted("EUR_WIRE") + ", " + Quoted("amount_give") + ": " + Quoted("100") + ", " + Quoted("padding") + ": " + Quoted(new string('a', RateEndpoints.MaxQuoteBodyBytes + 1)) + "}", "invalid_request", ["body"]);

    [Fact]
    public async Task A_receive_amount_that_rounds_to_zero_is_422_amount_too_small() =>
        await ShouldBe422Async("""{"give": "RUB_WIRE", "receive": "EUR_WIRE", "amount_give": "0.01", "bank_give_id": 1}""", "amount_too_small", ["body.amount_give"]); // 0.01 * 0.01

    // --- 503 (INV-5) --------------------------------------------------------------------------------

    private static readonly bool[] SourceDown = [true, false];

    // Every direction with each source failing in turn: the ECB serves USD and EUR, which every
    // direction needs; the Bank of Russia serves RUB, which only RUB_WIRE needs.
    public static TheoryData<string, string, bool> EveryDirectionWithEachSourceDown() =>
        [.. Directions.All.SelectMany(d => SourceDown.Select(ecbDown => (Codes.Of(d.Give), Codes.Of(d.Receive), ecbDown)))];

    [Theory]
    [MemberData(nameof(EveryDirectionWithEachSourceDown))]
    public async Task A_stale_source_refuses_exactly_the_directions_that_need_it(string give, string receive, bool ecbDown)
    {
        var refused = ecbDown || give == "RUB_WIRE";

        var (status, body) = await PostAsync(QuoteJson(give, receive), ecbWorks: !ecbDown, cbrWorks: ecbDown);

        if (refused)
        {
            status.ShouldBe(HttpStatusCode.ServiceUnavailable);
            body.RootElement.EnumerateObject().Select(p => (p.Name, p.Value.GetString())).ShouldBe([("error", "rates_unavailable"), ("message", RatesUnavailableText)]);
        }
        else
        {
            status.ShouldBe(HttpStatusCode.OK, body.RootElement.GetRawText());
        }
    }

    internal static async Task ShouldBe422Async(HttpResponseMessage response, string error, IReadOnlyList<string> fields)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBe(
            JsonSerializer.Serialize(new Dictionary<string, object> { ["error"] = error, ["fields"] = fields }));
    }

    private async Task ShouldBe422Async(string json, string error, IReadOnlyList<string> fields, string? databaseUrl = null)
    {
        using var factory = new TestAppFactory(await EnvironmentAsync(databaseUrl), services: FakeSources.Replace());
        using var client = factory.CreateClient();
        using var response = await SendAsync(client, json);

        await ShouldBe422Async(response, error, fields);
    }

    private async Task<(HttpStatusCode Status, JsonDocument Body)> PostAsync(string json, bool ecbWorks = true, bool cbrWorks = true)
    {
        using var factory = new TestAppFactory(await EnvironmentAsync(), services: FakeSources.Replace(ecbWorks, cbrWorks));
        using var client = factory.CreateClient();
        using var response = await SendAsync(client, json);
        return (response.StatusCode, JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));
    }

    internal static async Task<HttpResponseMessage> SendAsync(HttpClient client, string json)
    {
        var token = await TelegramAuth.SignInAsync(client, TelegramAuth.InitData(TestSettings.BotToken, 7_000_010, "Ann", null, DateTimeOffset.UtcNow.AddSeconds(-5)));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/rate") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    // A quote of 500 with the first seeded row of each field the direction needs.
    private static string QuoteJson(string give, string receive)
    {
        var ids = Directions.FieldsFor(Codes.ParseGive(give), Codes.ParseReceive(receive), Purpose.Quote).Required
            .Select(field => $", \"{field}\": {FirstValidId(field)}");
        return $$"""{"give": "{{give}}", "receive": "{{receive}}", "amount_give": "500"{{string.Concat(ids)}}}""";
    }

    private static int FirstValidId(string field) => field switch
    {
        DetailFields.BankGiveId => 1, // SBER, WIRE/RUB/RU
        DetailFields.BankReceiveId => 3, // LOVCEN, ATM/EUR/ME
        _ => 1,
    };

    private static string Quoted(string text) => JsonSerializer.Serialize(text);

    private static string Text(JsonElement element, string name) => element.GetProperty(name).GetString()!;

    private async Task<Dictionary<string, string?>> EnvironmentAsync(string? databaseUrl = null)
    {
        var environment = TestSettings.Production();
        environment["DATABASE_URL"] = databaseUrl ?? await database.CreateMigratedDatabaseUrlAsync();
        return environment;
    }
}
