using System.Text;
using Exchange.Domain.Directions;
using Exchange.Domain.Money;
using Exchange.Domain.Orders;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Exchange.Api.Data;

/// <summary>
/// The whole schema (spec 1.2, 1.5): snake_case names, money at its column scale, enumerations as
/// text with a CHECK constraint, and client PII as AES-GCM ciphertext bound to its column (INV-13).
/// </summary>
public sealed class ExchangeDbContext : DbContext
{
    /// <summary>Precision of an amount column: <c>NUMERIC(18,2)</c>.</summary>
    public const int AmountPrecisionDigits = 18;

    /// <summary>Precision of a rate column: <c>NUMERIC(24,12)</c>.</summary>
    public const int RatePrecisionDigits = 24;

    /// <summary>Precision of a percentage column: <c>NUMERIC(7,4)</c>.</summary>
    public const int PercentPrecisionDigits = 7;

    /// <summary>ISO 3166-1 alpha-2 length.</summary>
    public const int CountryCodeLengthChars = 2;

    /// <summary>ISO 4217 length.</summary>
    public const int CurrencyCodeLengthChars = 3;

    /// <summary>Longest code or enumeration value stored.</summary>
    public const int CodeMaxChars = 32;

    /// <summary>Longest city name.</summary>
    public const int NameMaxChars = 100;

    /// <summary>Storage width of a Telegram username: room to spare over what Telegram issues.</summary>
    public const int TelegramUsernameMaxChars = 64;

    /// <summary>Storage width of a Telegram first name: room to spare over what Telegram allows.</summary>
    public const int TelegramNameMaxChars = 256;

    /// <summary>The highest loyalty point count (spec 1.6).</summary>
    public const int LoyaltyPointsMax = 10;

    private readonly PiiCipher _cipher;

    /// <summary>Creates the context with the process's PII cipher.</summary>
    public ExchangeDbContext(DbContextOptions<ExchangeDbContext> options, PiiCipher cipher)
        : base(options) => _cipher = cipher;

    /// <summary>The cipher the model's PII converters use; part of the model cache key.</summary>
    public PiiCipher Cipher => _cipher;

    /// <summary>Cities.</summary>
    public DbSet<City> Cities => Set<City>();

    /// <summary>Banks.</summary>
    public DbSet<Bank> Banks => Set<Bank>();

    /// <summary>USDT networks.</summary>
    public DbSet<UsdtNetwork> UsdtNetworks => Set<UsdtNetwork>();

    /// <summary>Clients.</summary>
    public DbSet<AppUser> Users => Set<AppUser>();

    /// <summary>Exchange requests.</summary>
    public DbSet<Order> Orders => Set<Order>();

    /// <summary>Status history.</summary>
    public DbSet<OrderStatusChange> OrderStatusHistory => Set<OrderStatusChange>();

    /// <inheritdoc />
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        // The PII converters capture this context's cipher, so a model is built per cipher.
        optionsBuilder.ReplaceService<IModelCacheKeyFactory, CipherModelCacheKeyFactory>();
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<City>(city =>
        {
            city.ToTable("cities", table => table.HasCheckConstraint("ck_cities_markup_not_negative", "markup_percent >= 0"));
            city.Property(c => c.Name).HasMaxLength(NameMaxChars);
            city.HasIndex(c => c.Name).IsUnique();
            city.Property(c => c.Country).HasMaxLength(CountryCodeLengthChars).IsFixedLength();
            Percent(city.Property(c => c.MarkupPercent));
            city.Property(c => c.IsActive).HasDefaultValue(true);
        });

        modelBuilder.Entity<Bank>(bank =>
        {
            bank.ToTable("banks", table =>
            {
                table.HasCheckConstraint("ck_banks_channel_known", OneOf("channel", OrderCodes.AllChannels));
                table.HasCheckConstraint("ck_banks_markup_not_negative", "markup_percent >= 0");
            });
            bank.Property(b => b.Code).HasMaxLength(CodeMaxChars);
            bank.Property(b => b.Channel).HasMaxLength(CodeMaxChars)
                .HasConversion(new ValueConverter<BankChannel, string>(value => OrderCodes.Of(value), text => OrderCodes.ParseChannel(text)));
            bank.HasIndex(b => new { b.Code, b.Channel }).IsUnique();
            bank.Property(b => b.Currency).HasMaxLength(CurrencyCodeLengthChars).IsFixedLength();
            bank.Property(b => b.Country).HasMaxLength(CountryCodeLengthChars).IsFixedLength();
            Percent(bank.Property(b => b.MarkupPercent));
            bank.Property(b => b.IsActive).HasDefaultValue(true);
        });

        modelBuilder.Entity<UsdtNetwork>(network =>
        {
            network.ToTable("usdt_networks", table => table.HasCheckConstraint("ck_usdt_networks_fee_not_negative", "fee_percent >= 0"));
            network.Property(n => n.Code).HasMaxLength(CodeMaxChars);
            network.HasIndex(n => n.Code).IsUnique();
            Percent(network.Property(n => n.FeePercent));
            network.Property(n => n.IsActive).HasDefaultValue(true);
        });

        modelBuilder.Entity<AppUser>(user =>
        {
            user.ToTable("users", table => table.HasCheckConstraint(
                "ck_users_loyalty_points_range", $"loyalty_points BETWEEN 0 AND {LoyaltyPointsMax}"));
            user.HasIndex(u => u.TelegramId).IsUnique();
            user.Property(u => u.Username).HasMaxLength(TelegramUsernameMaxChars);
            user.Property(u => u.FirstName).HasMaxLength(TelegramNameMaxChars);
            user.Property(u => u.LoyaltyPoints).HasDefaultValue(0);
            user.Property(u => u.CreatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<Order>(order =>
        {
            order.ToTable("orders", table =>
            {
                table.HasCheckConstraint("ck_orders_status_known", OneOf("status", OrderCodes.AllStatuses));
                table.HasCheckConstraint("ck_orders_give_code_known", OneOf("give_code", Codes.AllGive));
                table.HasCheckConstraint("ck_orders_receive_code_known", OneOf("receive_code", Codes.AllReceive));
                table.HasCheckConstraint("ck_orders_usdt_transfer_time_known", OneOf("usdt_transfer_time", Codes.AllTransferTimes));
                table.HasCheckConstraint("ck_orders_amount_give_positive", "amount_give > 0");
                table.HasCheckConstraint("ck_orders_amount_receive_positive", "amount_receive > 0");
                table.HasCheckConstraint("ck_orders_base_rate_positive", "base_rate > 0");
                table.HasCheckConstraint("ck_orders_effective_rate_positive", "effective_rate > 0");
            });
            order.HasOne<AppUser>().WithMany().HasForeignKey(o => o.UserId).OnDelete(DeleteBehavior.Restrict);
            order.HasIndex(o => o.UserId);
            order.Property(o => o.Status).HasMaxLength(CodeMaxChars)
                .HasConversion(new ValueConverter<OrderStatus, string>(value => OrderCodes.Of(value), text => OrderCodes.ParseStatus(text)));
            order.Property(o => o.Give).HasColumnName("give_code").HasMaxLength(CodeMaxChars)
                .HasConversion(new ValueConverter<GiveCode, string>(value => Codes.Of(value), text => Codes.ParseGive(text)));
            order.Property(o => o.Receive).HasColumnName("receive_code").HasMaxLength(CodeMaxChars)
                .HasConversion(new ValueConverter<ReceiveCode, string>(value => Codes.Of(value), text => Codes.ParseReceive(text)));
            order.Property(o => o.UsdtTransferTime).HasMaxLength(CodeMaxChars)
                .HasConversion(new ValueConverter<UsdtTransferTime, string>(value => Codes.Of(value), text => Codes.ParseTransferTime(text)));
            Amount(order.Property(o => o.AmountGive));
            Amount(order.Property(o => o.AmountReceive));
            order.Property(o => o.RateProvider).HasMaxLength(CodeMaxChars);
            Rate(order.Property(o => o.BaseRate));
            Rate(order.Property(o => o.EffectiveRate));
            Percent(order.Property(o => o.CityMarkupPercent));
            Percent(order.Property(o => o.BankGiveMarkupPercent));
            Percent(order.Property(o => o.BankReceiveMarkupPercent));
            Percent(order.Property(o => o.NetworkGiveFeePercent));
            Percent(order.Property(o => o.NetworkReceiveFeePercent));
            Percent(order.Property(o => o.LoyaltyDiscountPercent));
            order.HasOne<City>().WithMany().HasForeignKey(o => o.CityId).OnDelete(DeleteBehavior.Restrict);
            order.HasOne<Bank>().WithMany().HasForeignKey(o => o.BankGiveId).OnDelete(DeleteBehavior.Restrict);
            order.HasOne<Bank>().WithMany().HasForeignKey(o => o.BankReceiveId).OnDelete(DeleteBehavior.Restrict);
            order.HasOne<UsdtNetwork>().WithMany().HasForeignKey(o => o.NetworkGiveId).OnDelete(DeleteBehavior.Restrict);
            order.HasOne<UsdtNetwork>().WithMany().HasForeignKey(o => o.NetworkReceiveId).OnDelete(DeleteBehavior.Restrict);
            Pii(order.Property(o => o.Iban), "orders.iban");
            Pii(order.Property(o => o.RecipientName), "orders.recipient_name");
            Pii(order.Property(o => o.WalletAddress), "orders.wallet_address");
            Pii(order.Property(o => o.PaymentPurpose), "orders.payment_purpose");
            order.Property(o => o.CreatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<OrderStatusChange>(change =>
        {
            change.ToTable("order_status_history", table =>
            {
                table.HasCheckConstraint("ck_order_status_history_from_status_known", OneOf("from_status", OrderCodes.AllStatuses));
                table.HasCheckConstraint("ck_order_status_history_to_status_known", OneOf("to_status", OrderCodes.AllStatuses));
                table.HasCheckConstraint("ck_order_status_history_actor_known", OneOf("actor", OrderCodes.AllActors));
            });
            change.HasOne<Order>().WithMany().HasForeignKey(c => c.OrderId).OnDelete(DeleteBehavior.Restrict);
            change.HasIndex(c => c.OrderId);
            var status = new ValueConverter<OrderStatus, string>(value => OrderCodes.Of(value), text => OrderCodes.ParseStatus(text));
            change.Property(c => c.FromStatus).HasMaxLength(CodeMaxChars).HasConversion(status);
            change.Property(c => c.ToStatus).HasMaxLength(CodeMaxChars).HasConversion(status);
            change.Property(c => c.Actor).HasMaxLength(CodeMaxChars)
                .HasConversion(new ValueConverter<Actor, string>(value => OrderCodes.Of(value), text => OrderCodes.ParseActor(text)));
            change.Property(c => c.CreatedAt).HasDefaultValueSql("now()");
        });

        ApplySnakeCaseNames(modelBuilder);
    }

    private static void Amount(PropertyBuilder<decimal> property) => property.HasPrecision(AmountPrecisionDigits, Quantize.AmountScaleDigits);

    private static void Rate(PropertyBuilder<decimal> property) => property.HasPrecision(RatePrecisionDigits, Quantize.RateScaleDigits);

    private static void Percent(PropertyBuilder<decimal> property) => property.HasPrecision(PercentPrecisionDigits, Quantize.PercentScaleDigits);

    private void Pii(PropertyBuilder<string?> property, string column)
    {
        var cipher = _cipher;
        // EF never hands a null to a converter: a NULL column stays NULL.
        ValueConverter converter = new ValueConverter<string, byte[]>(
            plaintext => cipher.Encrypt(plaintext, column),
            token => cipher.Decrypt(token, column));
        property.HasColumnType("bytea").HasConversion(converter);
    }

    // A CHECK accepting exactly the given values; NULL passes, as in SQL, for nullable columns.
    private static string OneOf(string column, IEnumerable<string> values) =>
        $"{column} IN ({string.Join(", ", values.Order(StringComparer.Ordinal).Select(value => $"'{value}'"))})";

    private static void ApplySnakeCaseNames(ModelBuilder modelBuilder)
    {
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                if (property.GetColumnName() == property.Name)
                {
                    property.SetColumnName(SnakeCase(property.Name));
                }
            }

            foreach (var key in entity.GetKeys())
            {
                key.SetName("pk_" + entity.GetTableName());
            }

            foreach (var foreignKey in entity.GetForeignKeys())
            {
                foreignKey.SetConstraintName($"fk_{entity.GetTableName()}_{SnakeCase(foreignKey.Properties[0].Name)}");
            }

            foreach (var index in entity.GetIndexes())
            {
                index.SetDatabaseName($"ix_{entity.GetTableName()}_{string.Join("_", index.Properties.Select(p => SnakeCase(p.Name)))}");
            }
        }
    }

    private static string SnakeCase(string name)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0)
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(name[i]));
        }

        return builder.ToString();
    }

    private sealed class CipherModelCacheKeyFactory : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime) =>
            context is ExchangeDbContext exchange ? (context.GetType(), exchange.Cipher, designTime) : (context.GetType(), designTime);
    }
}
