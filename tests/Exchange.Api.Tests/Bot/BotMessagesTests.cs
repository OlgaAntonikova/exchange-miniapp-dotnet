using Exchange.Api.Bot;
using Exchange.Api.Data;
using Exchange.Api.Locale;
using Exchange.Api.Tests.Data;
using Exchange.Domain.Directions;
using Exchange.Domain.Orders;
using Shouldly;

namespace Exchange.Api.Tests.Bot;

/// <summary>
/// Spec 1.5: the operator's message names the order, the client, the amounts, the rate, the
/// reference rows by name and every detail; its buttons are exactly the statuses that may come
/// next. The texts here are English stand-ins for <c>ru.json</c>, so the assertions read plainly.
/// </summary>
public sealed class BotMessagesTests(PostgresFixture database)
{
    private const long OperatorChat = -1001234;

    private static readonly LocaleTexts Texts = LocaleTexts.Load(
        System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["operator_new_order"] = "New order {order_id}",
            ["operator_order_status"] = "Order {order_id}: {status}",
            ["client_status_changed"] = "Your order {order_id}: {status}",
            ["label_client"] = "Client",
            ["label_gives"] = "Gives",
            ["label_receives"] = "Receives",
            ["label_rate"] = "Rate",
            ["label_city"] = "City",
            ["label_bank_give"] = "Sender bank",
            ["label_bank_receive"] = "ATM bank",
            ["label_network_give"] = "Network (gives)",
            ["label_network_receive"] = "Network (receives)",
            ["label_usdt_transfer_time"] = "USDT transfer",
            ["label_iban"] = "IBAN",
            ["label_recipient_name"] = "Recipient",
            ["label_wallet_address"] = "Wallet",
            ["label_payment_purpose"] = "Purpose",
            ["status_PENDING"] = "new",
            ["status_CONFIRMED"] = "confirmed",
            ["status_PROCESSING"] = "processing",
            ["status_COMPLETED"] = "completed",
            ["status_CANCELLED"] = "cancelled",
            ["button_CONFIRMED"] = "Confirm",
            ["button_PROCESSING"] = "Start",
            ["button_COMPLETED"] = "Complete",
            ["button_CANCELLED"] = "Cancel",
            ["transfer_now"] = "now",
            ["transfer_at_meeting"] = "at the meeting",
        }),
        []);

    private static readonly AppUser Client = new() { Id = 1, TelegramId = 527000001, Username = "ivan_p", FirstName = "Ivan" };

    [Fact]
    public async Task The_operators_new_order_message_carries_everything_the_deal_needs()
    {
        var (context, _) = await database.CreateMigratedAsync(new PiiCipher(new byte[PiiCipher.KeyLengthBytes]));
        await using var db = context;
        var order = new Order
        {
            Id = 7,
            Status = OrderStatus.Pending,
            Give = GiveCode.Usdt,
            Receive = ReceiveCode.EurAtm,
            AmountGive = 1000.00m,
            AmountReceive = 892.50m,
            EffectiveRate = 0.892500000000m,
            RateProvider = "ecb",
            NetworkGiveId = 3,
            BankReceiveId = 5,
            UsdtTransferTime = UsdtTransferTime.AtMeeting,
            Iban = "ME25505000012345678951",
            RecipientName = "Ivan Petrov",
        };

        var message = await new BotMessages(Texts, db).OperatorNewOrderAsync(order, Client, OperatorChat, TestContext.Current.CancellationToken);

        message.ChatId.ShouldBe(OperatorChat);
        message.Text.ShouldBe(string.Join('\n',
            "New order 7",
            "Client: @ivan_p (Ivan, id 527000001)",
            "Gives: 1000.00 USDT",
            "Receives: 892.50 EUR_ATM",
            "Rate: 0.892500000000 (ecb)",
            "ATM bank: ERSTE",
            "Network (gives): ERC20",
            "USDT transfer: at the meeting",
            "IBAN: ME25505000012345678951",
            "Recipient: Ivan Petrov"));
        message.Buttons.ShouldBe([new BotButton("Confirm", "order:7:CONFIRMED"), new BotButton("Cancel", "order:7:CANCELLED")]);
    }

    [Fact]
    public async Task City_sender_bank_receive_network_wallet_and_purpose_are_named_too()
    {
        var (context, _) = await database.CreateMigratedAsync(new PiiCipher(new byte[PiiCipher.KeyLengthBytes]));
        await using var db = context;
        var messages = new BotMessages(Texts, db);
        var cash = new Order { Id = 8, Give = GiveCode.RubWire, Receive = ReceiveCode.EurCash, RateProvider = "cbr+ecb", BankGiveId = 2, CityId = 2 };
        var usdt = new Order { Id = 9, Give = GiveCode.EurCash, Receive = ReceiveCode.Usdt, RateProvider = "ecb", NetworkReceiveId = 2, WalletAddress = "TQn9Y2khEsLJW1ChVWFMSMeRDow5KcbLSE", PaymentPurpose = null };
        var wire = new Order { Id = 10, Give = GiveCode.UsdCash, Receive = ReceiveCode.EurWire, RateProvider = "ecb", Iban = "ME25505000012345678951", RecipientName = "Ivan", PaymentPurpose = "Invoice 7" };

        (await messages.OperatorNewOrderAsync(cash, Client, OperatorChat, TestContext.Current.CancellationToken)).Text.ShouldContain("City: Budva\nSender bank: TINKOFF");
        (await messages.OperatorNewOrderAsync(usdt, Client, OperatorChat, TestContext.Current.CancellationToken)).Text.ShouldEndWith("Network (receives): TON\nWallet: TQn9Y2khEsLJW1ChVWFMSMeRDow5KcbLSE");
        (await messages.OperatorNewOrderAsync(wire, Client, OperatorChat, TestContext.Current.CancellationToken)).Text.ShouldEndWith("Purpose: Invoice 7");
    }

    [Theory]
    [InlineData("ivan_p", "Ivan", "@ivan_p (Ivan, id 527000001)")]
    [InlineData(null, "Ivan", "Ivan (id 527000001)")]
    [InlineData("", "Ivan", "Ivan (id 527000001)")]
    [InlineData("ivan_p", null, "@ivan_p (, id 527000001)")]
    public void The_client_handle_shows_the_username_when_there_is_one(string? username, string? firstName, string handle) =>
        BotMessages.ClientHandle(new AppUser { TelegramId = 527000001, Username = username, FirstName = firstName }).ShouldBe(handle);

    [Theory]
    [InlineData(OrderStatus.Confirmed, "confirmed", "Start,Cancel")]
    [InlineData(OrderStatus.Processing, "processing", "Complete,Cancel")]
    [InlineData(OrderStatus.Completed, "completed", "")]
    [InlineData(OrderStatus.Cancelled, "cancelled", "")]
    public void A_status_change_messages_the_client_and_offers_the_operator_what_may_come_next(OrderStatus status, string name, string buttons)
    {
        var messages = new BotMessages(Texts, null!);
        var order = new Order { Id = 7, Status = status, RateProvider = "ecb" };

        var client = messages.ClientStatusChanged(order, Client);
        var operatorMessage = messages.OperatorStatusChanged(order, OperatorChat);

        client.ChatId.ShouldBe(Client.TelegramId);
        client.Text.ShouldBe($"Your order 7: {name}");
        client.Buttons.ShouldBeEmpty();
        operatorMessage.ChatId.ShouldBe(OperatorChat);
        operatorMessage.Text.ShouldBe($"Order 7: {name}");
        string.Join(',', operatorMessage.Buttons.Select(b => b.Text)).ShouldBe(buttons);
    }

    [Theory]
    [InlineData("order:7:CONFIRMED", 7, OrderStatus.Confirmed)]
    [InlineData("order:123:CANCELLED", 123, OrderStatus.Cancelled)]
    public void A_status_callback_is_read_back(string data, int orderId, OrderStatus status) =>
        BotMessages.ParseCallback(data).ShouldBe((orderId, status));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("order:7")]
    [InlineData("order:7:CONFIRMED:x")]
    [InlineData("other:7:CONFIRMED")]
    [InlineData("order:-7:CONFIRMED")]
    [InlineData("order:x:CONFIRMED")]
    [InlineData("order:7:confirmed")]
    [InlineData("order:7:DONE")]
    [InlineData("order:99999999999:CONFIRMED")]
    public void Anything_else_is_not_a_status_callback(string? data) =>
        BotMessages.ParseCallback(data).ShouldBeNull();

    [Fact]
    public void Callback_data_fits_telegrams_64_byte_limit()
    {
        System.Text.Encoding.UTF8.GetByteCount(BotMessages.CallbackData(int.MaxValue, OrderStatus.Processing)).ShouldBeLessThanOrEqualTo(64);
    }
}
