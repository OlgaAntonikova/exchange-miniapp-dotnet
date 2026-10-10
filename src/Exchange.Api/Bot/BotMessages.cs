using System.Globalization;
using Exchange.Api.Data;
using Exchange.Api.Locale;
using Exchange.Domain.Directions;
using Exchange.Domain.Orders;
using Microsoft.EntityFrameworkCore;

namespace Exchange.Api.Bot;

/// <summary>
/// The texts the bot sends about an order (spec 1.5), every word from <c>ru.json</c> (spec 1.7).
/// The operator's message carries the client's details in full - IBAN, recipient, wallet,
/// purpose - because the operator completes the deal by hand; it goes to the operator chat only and
/// is never logged. Buttons offer exactly the statuses <see cref="StatusMachine"/> allows next.
/// </summary>
public sealed class BotMessages(LocaleTexts texts, ExchangeDbContext db)
{
    /// <summary>The prefix of a status button's callback data: <c>order:&lt;id&gt;:&lt;STATUS&gt;</c>.</summary>
    public const string CallbackPrefix = "order";

    /// <summary>What separates the parts of the callback data.</summary>
    public const char CallbackSeparator = ':';

    /// <summary>The callback data of the button that moves <paramref name="orderId"/> to <paramref name="status"/>.</summary>
    public static string CallbackData(int orderId, OrderStatus status) =>
        string.Join(CallbackSeparator, CallbackPrefix, orderId.ToString(CultureInfo.InvariantCulture), OrderCodes.Of(status));

    /// <summary>The order and status a callback names, if it is one of ours.</summary>
    public static (int OrderId, OrderStatus Status)? ParseCallback(string? data)
    {
        var parts = (data ?? string.Empty).Split(CallbackSeparator);
        return parts.Length == 3 && parts[0] == CallbackPrefix
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            && OrderCodes.TryParseStatus(parts[2], out var status)
            ? (id, status)
            : null;
    }

    /// <summary>The buttons for every status the operator may move the order to next.</summary>
    public IReadOnlyList<BotButton> Buttons(int orderId, OrderStatus current) =>
        [.. StatusMachine.Next(current).Select(next => new BotButton(texts.Text(LocaleKeys.Button(OrderCodes.Of(next))), CallbackData(orderId, next)))];

    /// <summary>The operator's message about a new order.</summary>
    public async Task<OutgoingMessage> OperatorNewOrderAsync(Order order, AppUser client, long operatorChatId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(client);
        var lines = new List<string>
        {
            texts.Text(LocaleKeys.OperatorNewOrder, Values(("order_id", Id(order)))),
            Line("label_client", ClientHandle(client)),
            Line("label_gives", Plain(order.AmountGive) + " " + Codes.Of(order.Give)),
            Line("label_receives", Plain(order.AmountReceive) + " " + Codes.Of(order.Receive)),
            Line("label_rate", Plain(order.EffectiveRate) + " (" + order.RateProvider + ")"),
        };
        if (order.CityId is { } city)
        {
            lines.Add(Line("label_city", await db.Cities.Where(c => c.Id == city).Select(c => c.Name).SingleAsync(cancellationToken).ConfigureAwait(false)));
        }

        if (order.BankGiveId is { } bankGive)
        {
            lines.Add(Line("label_bank_give", await db.Banks.Where(b => b.Id == bankGive).Select(b => b.Code).SingleAsync(cancellationToken).ConfigureAwait(false)));
        }

        if (order.BankReceiveId is { } bankReceive)
        {
            lines.Add(Line("label_bank_receive", await db.Banks.Where(b => b.Id == bankReceive).Select(b => b.Code).SingleAsync(cancellationToken).ConfigureAwait(false)));
        }

        if (order.NetworkGiveId is { } networkGive)
        {
            lines.Add(Line("label_network_give", await db.UsdtNetworks.Where(n => n.Id == networkGive).Select(n => n.Code).SingleAsync(cancellationToken).ConfigureAwait(false)));
        }

        if (order.NetworkReceiveId is { } networkReceive)
        {
            lines.Add(Line("label_network_receive", await db.UsdtNetworks.Where(n => n.Id == networkReceive).Select(n => n.Code).SingleAsync(cancellationToken).ConfigureAwait(false)));
        }

        if (order.UsdtTransferTime is { } time)
        {
            lines.Add(Line("label_usdt_transfer_time", texts.Text(LocaleKeys.Transfer(Codes.Of(time)))));
        }

        foreach (var (label, value) in new[]
        {
            ("label_iban", order.Iban),
            ("label_recipient_name", order.RecipientName),
            ("label_wallet_address", order.WalletAddress),
            ("label_payment_purpose", order.PaymentPurpose),
        })
        {
            if (value is not null)
            {
                lines.Add(Line(label, value));
            }
        }

        return new OutgoingMessage(operatorChatId, string.Join('\n', lines), Buttons(order.Id, order.Status));
    }

    /// <summary>The client's message about their order's new status.</summary>
    public OutgoingMessage ClientStatusChanged(Order order, AppUser client)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(client);
        return new OutgoingMessage(client.TelegramId, texts.Text(LocaleKeys.ClientStatusChanged, Values(("order_id", Id(order)), ("status", StatusName(order.Status)))), []);
    }

    /// <summary>The operator's message about an order's new status, with the buttons for what may come next.</summary>
    public OutgoingMessage OperatorStatusChanged(Order order, long operatorChatId)
    {
        ArgumentNullException.ThrowIfNull(order);
        return new OutgoingMessage(operatorChatId, texts.Text(LocaleKeys.OperatorOrderStatus, Values(("order_id", Id(order)), ("status", StatusName(order.Status)))), Buttons(order.Id, order.Status));
    }

    /// <summary>How the operator sees the client: <c>@username (first name, id N)</c>, or <c>first name (id N)</c> without a username.</summary>
    public static string ClientHandle(AppUser client)
    {
        ArgumentNullException.ThrowIfNull(client);
        var name = client.FirstName ?? string.Empty;
        var id = client.TelegramId.ToString(CultureInfo.InvariantCulture);
        return client.Username is { Length: > 0 } username ? $"@{username} ({name}, id {id})" : $"{name} (id {id})";
    }

    private string Line(string labelKey, string value) => texts.Text(labelKey) + ": " + value;

    private string StatusName(OrderStatus status) => texts.Text(LocaleKeys.Status(OrderCodes.Of(status)));

    private static string Id(Order order) => order.Id.ToString(CultureInfo.InvariantCulture);

    private static string Plain(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private static Dictionary<string, string> Values(params (string Name, string Value)[] values) => values.ToDictionary(v => v.Name, v => v.Value);
}
