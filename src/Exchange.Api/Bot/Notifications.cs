using System.Threading.Channels;
using Exchange.Api.Data;
using Exchange.Api.Settings;

namespace Exchange.Api.Bot;

/// <summary>A bot message to deliver, with what it is about for the log (never its text).</summary>
/// <param name="Message">The message.</param>
/// <param name="Operation">What it is: <c>new order</c>, <c>status change</c>...</param>
/// <param name="OrderId">The order it is about.</param>
public sealed record Notification(OutgoingMessage Message, string Operation, int OrderId);

/// <summary>
/// Bot messages waiting for delivery, off the request path (spec 1.5). A full queue drops the
/// message with an <c>ERROR</c>: the order stands either way.
/// </summary>
public sealed partial class NotificationOutbox(ILogger<NotificationOutbox> logger)
{
    /// <summary>Messages waiting before new ones are dropped.</summary>
    public const int CapacityMessages = 1000;

    private readonly Channel<Notification> _queue = Channel.CreateBounded<Notification>(new BoundedChannelOptions(CapacityMessages)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
    });

    /// <summary>The messages to deliver, in order.</summary>
    public ChannelReader<Notification> Reader => _queue.Reader;

    /// <summary>Queues a message; never throws, never blocks.</summary>
    public void Enqueue(Notification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (!_queue.Writer.TryWrite(notification))
        {
            LogDropped(logger, notification.Operation, notification.OrderId);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Notification dropped, queue full: {Operation} for order {OrderId}")]
    private static partial void LogDropped(ILogger logger, string operation, int orderId);
}

/// <summary>
/// Delivers queued bot messages (spec 1.5): 3 attempts with exponential backoff (1 s, then 2 s). A
/// retry is a <c>WARNING</c>; giving up is an <c>ERROR</c> naming the operation and the order - and
/// so an operator alert - and the order stands. In demo mode nothing goes to Telegram: each message
/// becomes one <c>INFO</c> record naming its kind and order (spec 1.9). The text is never logged.
/// </summary>
public sealed partial class NotificationDispatcher(NotificationOutbox queue, IBotClient bot, AppSettings settings, TimeProvider time, ILogger<NotificationDispatcher> logger) : BackgroundService
{
    /// <summary>How many times a message is tried.</summary>
    public const int Attempts = 3;

    /// <summary>The wait before the second attempt; each later wait doubles.</summary>
    public static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(1);

    private const int BackoffFactor = 2;

    /// <summary>Delivers one message; true once Telegram took it. Never throws, except for a stopping app.</summary>
    public async Task<bool> DeliverAsync(Notification notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (settings.Environment == AppEnvironment.Demo)
        {
            LogSuppressed(logger, notification.Operation, notification.OrderId);
            return true;
        }

        var delay = BaseDelay;
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            try
            {
                await bot.SendMessageAsync(notification.Message, cancellationToken).ConfigureAwait(false);
                LogSent(logger, notification.Operation, notification.OrderId, attempt);
                return true;
            }
            catch (BotException failure) when (attempt < Attempts)
            {
                LogRetry(logger, notification.Operation, notification.OrderId, failure.Reason, attempt);
                await Task.Delay(delay, time, cancellationToken).ConfigureAwait(false);
                delay *= BackoffFactor;
            }
            catch (BotException failure)
            {
                // The stack is safe: a BotException carries a reason code and never the URL.
                LogFailed(logger, failure, notification.Operation, notification.OrderId, failure.Reason, attempt);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // Only a BotException is expected; anything else is a bug, logged with its stack and
                // not retried.
                var reason = "unexpected_" + error.GetType().Name;
                LogFailed(logger, error, notification.Operation, notification.OrderId, reason, attempt);
                return false;
            }
        }

        return false;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var notification in queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            await DeliverAsync(notification, stoppingToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Notification sent: {Operation} for order {OrderId} on attempt {Attempt}")]
    private static partial void LogSent(ILogger logger, string operation, int orderId, int attempt);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Notification retry: {Operation} for order {OrderId} failed with {Reason} on attempt {Attempt}")]
    private static partial void LogRetry(ILogger logger, string operation, int orderId, string reason, int attempt);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Notification failed: {Operation} for order {OrderId} with {Reason} after {Attempts} attempts")]
    private static partial void LogFailed(ILogger logger, Exception error, string operation, int orderId, string reason, int attempts);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Notification suppressed in demo mode: {Operation} for order {OrderId}")]
    private static partial void LogSuppressed(ILogger logger, string operation, int orderId);
}

/// <summary>
/// What the bot says when an order is placed or changes status: the operator gets the new order
/// with its buttons; on every status change the client gets a message and the operator gets the
/// new status with the next buttons. Building a message never fails the order: a fault is logged
/// at <c>ERROR</c> and the change stands.
/// </summary>
public sealed partial class OrderNotifications(BotMessages messages, NotificationOutbox queue, AppSettings settings, ILogger<OrderNotifications> logger)
{
    /// <summary>The operation name of a new order's message.</summary>
    public const string NewOrderOperation = "new order";

    /// <summary>The operation name of a client's status message.</summary>
    public const string ClientStatusOperation = "client status";

    /// <summary>The operation name of the operator's status message.</summary>
    public const string OperatorStatusOperation = "operator status";

    // Demo mode has no operator chat; its suppressed messages still name a chat, which goes nowhere.
    private long OperatorChat => settings.OperatorChatId ?? 0;

    /// <summary>
    /// Queues the operator's message about a new order. It takes no cancellation: the order is
    /// committed, and a client who closes the app must not cost the operator the message. Any fault
    /// is an <c>ERROR</c>, never an exception out of here.
    /// </summary>
    public async Task OrderCreatedAsync(Order order, AppUser client)
    {
        ArgumentNullException.ThrowIfNull(order);
        try
        {
            queue.Enqueue(new Notification(await messages.OperatorNewOrderAsync(order, client, OperatorChat, CancellationToken.None).ConfigureAwait(false), NewOrderOperation, order.Id));
        }
        catch (Exception error)
        {
            LogBuildFailed(logger, error, NewOrderOperation, order.Id);
        }
    }

    /// <summary>Queues the client's and the operator's messages about a status change.</summary>
    public void StatusChanged(Order order, AppUser client)
    {
        ArgumentNullException.ThrowIfNull(order);
        Enqueue(() => messages.ClientStatusChanged(order, client), ClientStatusOperation, order.Id);
        Enqueue(() => messages.OperatorStatusChanged(order, OperatorChat), OperatorStatusOperation, order.Id);
    }

    // Each message on its own: one that cannot be built does not stop the other, and the ERROR
    // names the one that failed.
    private void Enqueue(Func<OutgoingMessage> build, string operation, int orderId)
    {
        try
        {
            queue.Enqueue(new Notification(build(), operation, orderId));
        }
        catch (Exception error)
        {
            LogBuildFailed(logger, error, operation, orderId);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Notification not built: {Operation} for order {OrderId}")]
    private static partial void LogBuildFailed(ILogger logger, Exception error, string operation, int orderId);
}
