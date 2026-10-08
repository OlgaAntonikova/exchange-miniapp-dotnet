using System.Security.Cryptography;
using Exchange.Api.Data;
using Exchange.Domain.Orders;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;

namespace Exchange.Api.Tests.Data;

/// <summary>The PHASE-1 gate in code: the migration creates and seeds the schema of spec 1.2 and 1.5.</summary>
public sealed class MigrationTests(PostgresFixture database)
{
    private const string CheckViolation = "23514";

    private static readonly PiiCipher Cipher = new(RandomNumberGenerator.GetBytes(PiiCipher.KeyLengthBytes));

    [Fact]
    public async Task The_migration_seeds_the_reference_data_of_the_spec()
    {
        var (db, _) = await database.CreateMigratedAsync(Cipher);
        await using var context = db;
        var cancel = TestContext.Current.CancellationToken;

        var cities = await db.Cities.OrderBy(c => c.Id).ToListAsync(cancel);
        var banks = await db.Banks.OrderBy(b => b.Id).ToListAsync(cancel);
        var networks = await db.UsdtNetworks.OrderBy(n => n.Id).ToListAsync(cancel);

        // Spec 1.2, written out here independently of the migration.
        cities.Select(c => (c.Name, c.Country, c.MarkupPercent, c.IsActive)).ShouldBe(
        [
            ("Podgorica", "ME", 0.0m, true), ("Budva", "ME", 0.5m, true), ("Bar", "ME", 0.5m, true),
            ("Tivat", "ME", 0.7m, true), ("Kotor", "ME", 0.7m, true), ("Herceg Novi", "ME", 1.0m, true),
        ]);
        banks.Select(b => (b.Code, b.Channel, b.Currency, b.Country, b.MarkupPercent, b.IsActive)).ShouldBe(
        [
            ("SBER", BankChannel.Wire, "RUB", "RU", 0.0m, true), ("TINKOFF", BankChannel.Wire, "RUB", "RU", 0.5m, true),
            ("LOVCEN", BankChannel.Atm, "EUR", "ME", 0.0m, true), ("CKB", BankChannel.Atm, "EUR", "ME", 0.0m, true),
            ("ERSTE", BankChannel.Atm, "EUR", "ME", 0.5m, true),
        ]);
        networks.Select(n => (n.Code, n.FeePercent, n.IsActive)).ShouldBe([("TRC20", 0.3m, true), ("TON", 0.4m, true), ("ERC20", 1.0m, true)]);
        cities[1].MarkupPercent.Scale.ShouldBe((byte)4); // read back at the column's scale: "0.5000"
    }

    [Fact]
    public async Task The_model_and_the_migrations_agree()
    {
        var (db, _) = await database.CreateMigratedAsync(Cipher);
        await using var context = db;

        db.Database.HasPendingModelChanges().ShouldBeFalse("a model change has no migration");
        (await db.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_row_added_after_the_seed_gets_a_fresh_id()
    {
        var (db, _) = await database.CreateMigratedAsync(Cipher);
        await using var context = db;

        db.Cities.Add(new City { Name = "Ulcinj", Country = "ME", MarkupPercent = 1.0m });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        (await db.Cities.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(7);
    }

    [Theory]
    [InlineData("UPDATE banks SET channel = 'FAX' WHERE id = 1", "ck_banks_channel_known")]
    [InlineData("UPDATE banks SET markup_percent = -0.1 WHERE id = 1", "ck_banks_markup_not_negative")]
    [InlineData("UPDATE cities SET markup_percent = -1 WHERE id = 1", "ck_cities_markup_not_negative")]
    [InlineData("UPDATE usdt_networks SET fee_percent = -1 WHERE id = 1", "ck_usdt_networks_fee_not_negative")]
    [InlineData("INSERT INTO users (telegram_id, loyalty_points) VALUES (1, 11)", "ck_users_loyalty_points_range")]
    [InlineData("INSERT INTO users (telegram_id, loyalty_points) VALUES (1, -1)", "ck_users_loyalty_points_range")]
    public async Task The_database_refuses_values_outside_the_domain(string sql, string constraint)
    {
        var (db, connectionString) = await database.CreateMigratedAsync(Cipher);
        await using var context = db;

        var error = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(connectionString, sql));

        error.SqlState.ShouldBe(CheckViolation);
        error.ConstraintName.ShouldBe(constraint);
    }

    [Theory]
    [InlineData("status = 'BOGUS'", "ck_orders_status_known")]
    [InlineData("give_code = 'BTC'", "ck_orders_give_code_known")]
    [InlineData("receive_code = 'EUR_CHEQUE'", "ck_orders_receive_code_known")]
    [InlineData("usdt_transfer_time = 'later'", "ck_orders_usdt_transfer_time_known")]
    [InlineData("amount_give = 0", "ck_orders_amount_give_positive")]
    [InlineData("amount_receive = -1", "ck_orders_amount_receive_positive")]
    [InlineData("base_rate = 0", "ck_orders_base_rate_positive")]
    [InlineData("effective_rate = 0", "ck_orders_effective_rate_positive")]
    public async Task An_order_outside_the_domain_is_refused(string assignment, string constraint)
    {
        var (db, connectionString) = await database.CreateMigratedAsync(Cipher);
        await using var context = db;
        var order = await OrderRows.AddOrderAsync(db);

        var error = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(connectionString, $"UPDATE orders SET {assignment} WHERE id = {order.Id}"));

        error.SqlState.ShouldBe(CheckViolation);
        error.ConstraintName.ShouldBe(constraint);
    }

    [Theory]
    [InlineData("to_status = 'DONE'", "ck_order_status_history_to_status_known")]
    [InlineData("from_status = 'DONE'", "ck_order_status_history_from_status_known")]
    [InlineData("actor = 'robot'", "ck_order_status_history_actor_known")]
    public async Task A_history_row_outside_the_domain_is_refused(string assignment, string constraint)
    {
        var (db, connectionString) = await database.CreateMigratedAsync(Cipher);
        await using var context = db;
        var order = await OrderRows.AddOrderAsync(db);
        db.OrderStatusHistory.Add(new OrderStatusChange { OrderId = order.Id, ToStatus = OrderStatus.Pending, Actor = Actor.System });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var error = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(connectionString, $"UPDATE order_status_history SET {assignment}"));

        error.SqlState.ShouldBe(CheckViolation);
        error.ConstraintName.ShouldBe(constraint);
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
