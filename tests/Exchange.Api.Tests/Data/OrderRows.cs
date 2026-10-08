using Exchange.Api.Data;
using Exchange.Domain.Directions;
using Exchange.Domain.Orders;

namespace Exchange.Api.Tests.Data;

/// <summary>A valid order row to test the schema with (the order service arrives in PHASE-4).</summary>
internal static class OrderRows
{
    public const string Iban = "ME25505000012345678951";
    public const string Recipient = "Ivan Petrov";
    public const string Wallet = "TQn9Y2khEsLJW1ChVWFMSMeRDow5KcbLSE";
    public const string Purpose = "rent for October";

    private const long FirstTelegramId = 1_000_000;

    private static int _telegramIds;

    public static async Task<Order> AddOrderAsync(ExchangeDbContext db, Action<Order>? change = null)
    {
        var user = new AppUser { TelegramId = FirstTelegramId + Interlocked.Increment(ref _telegramIds), Username = "client" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var order = new Order
        {
            UserId = user.Id,
            Status = OrderStatus.Pending,
            Give = GiveCode.UsdCash,
            Receive = ReceiveCode.EurWire,
            AmountGive = 1000.00m,
            AmountReceive = 912.34m,
            RateProvider = "ecb",
            RateSnapshotAt = DateTimeOffset.UtcNow,
            BaseRate = 0.923456789012m,
            CityMarkupPercent = 0m,
            BankGiveMarkupPercent = 0m,
            BankReceiveMarkupPercent = 0m,
            NetworkGiveFeePercent = 0m,
            NetworkReceiveFeePercent = 0m,
            LoyaltyDiscountPercent = 0m,
            EffectiveRate = 0.912345678901m,
            Iban = Iban,
            RecipientName = Recipient,
            PaymentPurpose = Purpose,
        };
        change?.Invoke(order);
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }
}
