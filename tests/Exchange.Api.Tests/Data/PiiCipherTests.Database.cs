using System.Security.Cryptography;
using System.Text;
using Exchange.Api.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;

namespace Exchange.Api.Tests.Data;

/// <summary>
/// INV-13 in the database: the PII columns hold ciphertext; a wrong key, or a token moved into
/// another column, fails loudly instead of returning data.
/// </summary>
public sealed partial class PiiCipherTests(PostgresFixture database)
{
    private static readonly string[] PiiColumns = ["iban", "recipient_name", "wallet_address", "payment_purpose"];

    [Fact]
    public async Task Pii_columns_hold_ciphertext_and_read_back_as_plaintext()
    {
        var cipher = NewCipher();
        var (db, connectionString) = await database.CreateMigratedAsync(cipher);
        await using var context = db;
        var order = await OrderRows.AddOrderAsync(db, o => o.WalletAddress = OrderRows.Wallet);

        var raw = await ReadRawAsync(connectionString, order.Id);

        raw.Keys.ShouldBe(PiiColumns, ignoreOrder: true);
        var plaintexts = new Dictionary<string, string>
        {
            ["iban"] = OrderRows.Iban,
            ["recipient_name"] = OrderRows.Recipient,
            ["wallet_address"] = OrderRows.Wallet,
            ["payment_purpose"] = OrderRows.Purpose,
        };
        foreach (var (column, bytes) in raw)
        {
            bytes.ShouldNotBeNull(column);
            bytes[0].ShouldBe(PiiCipher.TokenVersion);
            Encoding.UTF8.GetString(bytes).ShouldNotContain(plaintexts[column]);
            cipher.Decrypt(bytes, "orders." + column).ShouldBe(plaintexts[column]);
        }

        await using var fresh = PostgresFixture.CreateContext(connectionString, cipher);
        var read = await fresh.Orders.SingleAsync(o => o.Id == order.Id, TestContext.Current.CancellationToken);
        (read.Iban, read.RecipientName, read.WalletAddress, read.PaymentPurpose).ShouldBe((OrderRows.Iban, OrderRows.Recipient, OrderRows.Wallet, OrderRows.Purpose));
    }

    [Fact]
    public async Task A_missing_value_stays_null_in_the_database()
    {
        var (db, connectionString) = await database.CreateMigratedAsync(NewCipher());
        await using var context = db;
        var order = await OrderRows.AddOrderAsync(db, o => o.PaymentPurpose = null);

        (await ReadRawAsync(connectionString, order.Id))["payment_purpose"].ShouldBeNull();
    }

    [Fact]
    public async Task Reading_with_another_key_fails_loudly()
    {
        var (db, connectionString) = await database.CreateMigratedAsync(NewCipher());
        await using var context = db;
        var order = await OrderRows.AddOrderAsync(db);

        await using var other = PostgresFixture.CreateContext(connectionString, NewCipher());
        var error = await Should.ThrowAsync<Exception>(() => other.Orders.SingleAsync(o => o.Id == order.Id, TestContext.Current.CancellationToken));

        Chain(error).OfType<PiiDecryptionException>().ShouldNotBeEmpty();
        error.ToString().ShouldNotContain(OrderRows.Iban);
    }

    [Fact]
    public async Task A_token_moved_to_another_column_fails_loudly()
    {
        var cipher = NewCipher();
        var (db, connectionString) = await database.CreateMigratedAsync(cipher);
        await using var context = db;
        var order = await OrderRows.AddOrderAsync(db);
        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = new NpgsqlCommand($"UPDATE orders SET recipient_name = iban WHERE id = {order.Id}", connection);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await using var fresh = PostgresFixture.CreateContext(connectionString, cipher);
        var error = await Should.ThrowAsync<Exception>(() => fresh.Orders.SingleAsync(o => o.Id == order.Id, TestContext.Current.CancellationToken));

        Chain(error).OfType<PiiDecryptionException>().ShouldNotBeEmpty();
    }

    private static PiiCipher NewCipher() => new(RandomNumberGenerator.GetBytes(PiiCipher.KeyLengthBytes));

    private static async Task<Dictionary<string, byte[]?>> ReadRawAsync(string connectionString, int orderId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand($"SELECT {string.Join(", ", PiiColumns)} FROM orders WHERE id = {orderId}", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        (await reader.ReadAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();
        return PiiColumns.Select((column, index) => (column, value: reader.IsDBNull(index) ? null : (byte[])reader.GetValue(index)))
            .ToDictionary(pair => pair.column, pair => pair.value);
    }

    private static IEnumerable<Exception> Chain(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            yield return current;
        }
    }
}
