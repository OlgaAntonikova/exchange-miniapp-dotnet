using System.Security.Cryptography;
using Exchange.Api.Data;
using Exchange.Api.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;

namespace Exchange.Api.Tests;

/// <summary>INV-14 in the database: money columns are exact NUMERIC at their scale, and values round-trip unchanged.</summary>
public sealed partial class MoneyTests(PostgresFixture database)
{
    private static readonly PiiCipher Cipher = new(RandomNumberGenerator.GetBytes(PiiCipher.KeyLengthBytes));

    [Fact]
    public async Task Extreme_amounts_rates_and_percentages_round_trip_exactly_with_their_scale()
    {
        var (db, connectionString) = await database.CreateMigratedAsync(Cipher);
        await using var context = db;
        var order = await OrderRows.AddOrderAsync(db, o =>
        {
            o.AmountGive = 9999999999999999.99m; // the largest NUMERIC(18,2)
            o.AmountReceive = 0.01m;
            o.BaseRate = 999999999999.999999999999m; // the largest NUMERIC(24,12)
            o.EffectiveRate = 0.000000000001m;
            o.CityMarkupPercent = 999.9999m; // the largest NUMERIC(7,4)
            o.LoyaltyDiscountPercent = 0.0001m;
        });

        await using var fresh = PostgresFixture.CreateContext(connectionString, Cipher);
        var read = await fresh.Orders.SingleAsync(o => o.Id == order.Id, TestContext.Current.CancellationToken);

        read.AmountGive.ShouldBe(9999999999999999.99m);
        read.AmountReceive.ShouldBe(0.01m);
        read.BaseRate.ShouldBe(999999999999.999999999999m);
        read.EffectiveRate.ShouldBe(0.000000000001m);
        read.CityMarkupPercent.ShouldBe(999.9999m);
        read.LoyaltyDiscountPercent.ShouldBe(0.0001m);
        read.AmountReceive.Scale.ShouldBe((byte)2);
        read.EffectiveRate.Scale.ShouldBe((byte)12);
        read.BankGiveMarkupPercent.ToString(System.Globalization.CultureInfo.InvariantCulture).ShouldBe("0.0000");
    }

    [Theory]
    [InlineData("orders", "amount_give", 18, 2)]
    [InlineData("orders", "amount_receive", 18, 2)]
    [InlineData("orders", "base_rate", 24, 12)]
    [InlineData("orders", "effective_rate", 24, 12)]
    [InlineData("orders", "city_markup_percent", 7, 4)]
    [InlineData("orders", "bank_give_markup_percent", 7, 4)]
    [InlineData("orders", "bank_receive_markup_percent", 7, 4)]
    [InlineData("orders", "network_give_fee_percent", 7, 4)]
    [InlineData("orders", "network_receive_fee_percent", 7, 4)]
    [InlineData("orders", "loyalty_discount_percent", 7, 4)]
    [InlineData("cities", "markup_percent", 7, 4)]
    [InlineData("banks", "markup_percent", 7, 4)]
    [InlineData("usdt_networks", "fee_percent", 7, 4)]
    public async Task Every_money_column_is_exact_numeric_at_its_scale(string table, string column, int precision, int scale)
    {
        var (db, connectionString) = await database.CreateMigratedAsync(Cipher);
        await using var context = db;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT data_type, numeric_precision, numeric_scale FROM information_schema.columns WHERE table_name = @table AND column_name = @column",
            connection);
        command.Parameters.AddWithValue("table", table);
        command.Parameters.AddWithValue("column", column);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);

        (await reader.ReadAsync(TestContext.Current.CancellationToken)).ShouldBeTrue(column);
        (reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2)).ShouldBe(("numeric", precision, scale));
    }
}
