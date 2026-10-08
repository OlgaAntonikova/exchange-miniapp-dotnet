using System.Net;
using System.Text.Json;
using Exchange.Api.Settings;
using Exchange.Api.Tests.Data;
using Exchange.Api.Tests.Settings;
using Npgsql;
using Shouldly;

namespace Exchange.Api.Tests.Reference;

/// <summary>
/// Spec 1.10 and the PHASE-1 gate: the reference endpoints return active rows only, in the shape of
/// section 1.10, with money as strings at their column scale (INV-14).
/// </summary>
public sealed class ReferenceApiTests(PostgresFixture database)
{
    [Fact]
    public async Task Cities_are_the_active_seed_with_markups_as_strings()
    {
        var body = await GetAsync(await database.CreateMigratedDatabaseUrlAsync(), "/api/cities");

        body.ShouldBe("""
            [{"id":1,"name":"Podgorica","markup_percent":"0.0000"},{"id":2,"name":"Budva","markup_percent":"0.5000"},{"id":3,"name":"Bar","markup_percent":"0.5000"},{"id":4,"name":"Tivat","markup_percent":"0.7000"},{"id":5,"name":"Kotor","markup_percent":"0.7000"},{"id":6,"name":"Herceg Novi","markup_percent":"1.0000"}]
            """);
    }

    [Fact]
    public async Task Banks_are_the_active_seed_with_their_channel_codes()
    {
        var body = await GetAsync(await database.CreateMigratedDatabaseUrlAsync(), "/api/banks");

        body.ShouldBe("""
            [{"id":1,"code":"SBER","channel":"WIRE","currency":"RUB","country":"RU","markup_percent":"0.0000"},{"id":2,"code":"TINKOFF","channel":"WIRE","currency":"RUB","country":"RU","markup_percent":"0.5000"},{"id":3,"code":"LOVCEN","channel":"ATM","currency":"EUR","country":"ME","markup_percent":"0.0000"},{"id":4,"code":"CKB","channel":"ATM","currency":"EUR","country":"ME","markup_percent":"0.0000"},{"id":5,"code":"ERSTE","channel":"ATM","currency":"EUR","country":"ME","markup_percent":"0.5000"}]
            """);
    }

    [Fact]
    public async Task Usdt_networks_are_the_active_seed_with_fees_as_strings()
    {
        var body = await GetAsync(await database.CreateMigratedDatabaseUrlAsync(), "/api/usdt-networks");

        body.ShouldBe("""
            [{"id":1,"code":"TRC20","fee_percent":"0.3000"},{"id":2,"code":"TON","fee_percent":"0.4000"},{"id":3,"code":"ERC20","fee_percent":"1.0000"}]
            """);
    }

    [Theory]
    [InlineData("cities", "/api/cities", 2)]
    [InlineData("banks", "/api/banks", 4)]
    [InlineData("usdt_networks", "/api/usdt-networks", 1)]
    public async Task An_inactive_row_is_not_listed(string table, string path, int inactiveId)
    {
        var url = await database.CreateMigratedDatabaseUrlAsync();
        await using (var connection = new NpgsqlConnection(DatabaseUrl.Convert(url).ConnectionString!.Reveal()))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = new NpgsqlCommand($"UPDATE {table} SET is_active = false WHERE id = {inactiveId}", connection);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        using var document = JsonDocument.Parse(await GetAsync(url, path));
        var ids = document.RootElement.EnumerateArray().Select(row => row.GetProperty("id").GetInt32()).ToList();

        ids.ShouldNotContain(inactiveId);
        ids.ShouldBe([.. ids.Order()]);
        ids.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Directions_publish_the_fourteen_pairs_with_sorted_field_names()
    {
        using var document = JsonDocument.Parse(await GetAsync(await database.CreateMigratedDatabaseUrlAsync(), "/api/directions"));
        var rows = document.RootElement.EnumerateArray().ToList();

        // D-026: give codes in the order of spec 1.1, and receive codes within each.
        rows.Select(r => (r.GetProperty("give").GetString(), r.GetProperty("receive").GetString())).ShouldBe(
        [
            ("RUB_WIRE", "EUR_CASH"), ("RUB_WIRE", "EUR_ATM"), ("RUB_WIRE", "EUR_WIRE"), ("RUB_WIRE", "EUR_INVOICE"), ("RUB_WIRE", "USDT"),
            ("USD_CASH", "EUR_CASH"), ("USD_CASH", "EUR_ATM"), ("USD_CASH", "EUR_WIRE"), ("USD_CASH", "EUR_INVOICE"),
            ("EUR_CASH", "USDT"),
            ("USDT", "EUR_CASH"), ("USDT", "EUR_ATM"), ("USDT", "EUR_WIRE"), ("USDT", "EUR_INVOICE"),
        ]);
        foreach (var row in rows)
        {
            row.EnumerateObject().Select(p => p.Name).ShouldBe(["give", "receive", "quote_fields", "order_fields", "optional_fields"]);
            foreach (var list in new[] { "quote_fields", "order_fields", "optional_fields" })
            {
                var names = row.GetProperty(list).EnumerateArray().Select(name => name.GetString()!).ToList();
                names.ShouldBe([.. names.Order(StringComparer.Ordinal)], list);
            }
        }

        // One row checked against spec 1.1 directly: USDT given for euros at an ATM.
        var usdtToAtm = rows.Single(r => r.GetProperty("give").GetString() == "USDT" && r.GetProperty("receive").GetString() == "EUR_ATM");
        Names(usdtToAtm, "quote_fields").ShouldBe(["bank_receive_id", "network_give_id"]);
        Names(usdtToAtm, "order_fields").ShouldBe(["bank_receive_id", "iban", "network_give_id", "recipient_name", "usdt_transfer_time"]);
        Names(usdtToAtm, "optional_fields").ShouldBeEmpty();
        var rubToWire = rows.Single(r => r.GetProperty("give").GetString() == "RUB_WIRE" && r.GetProperty("receive").GetString() == "EUR_WIRE");
        Names(rubToWire, "optional_fields").ShouldBe(["payment_purpose"]);
    }

    private static List<string> Names(JsonElement row, string list) =>
        [.. row.GetProperty(list).EnumerateArray().Select(name => name.GetString()!)];

    private static async Task<string> GetAsync(string databaseUrl, string path)
    {
        var environment = TestSettings.Production();
        environment["DATABASE_URL"] = databaseUrl;
        using var factory = new TestAppFactory(environment);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }
}
