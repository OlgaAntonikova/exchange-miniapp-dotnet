using Exchange.Api.Alerts;
using Exchange.Api.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Serilog;

namespace Exchange.Api.Tests.Logging;

/// <summary>
/// A minimal app with the app's own request pipeline (<see cref="RequestLogging.UseRequestLogging"/>)
/// and logging setup, plus endpoints that fail on purpose, which the real app does not have.
/// </summary>
internal sealed class RequestPipelineHarness : IAsyncDisposable
{
    public const int TokenTailChars = 35;

    public static readonly string BotToken = "4444444" + ":" + new string('Z', TokenTailChars);

    private static readonly Action<Microsoft.Extensions.Logging.ILogger, int, Exception?> ItemRead =
        LoggerMessage.Define<int>(LogLevel.Information, new EventId(1), "Item {Id} read");

    private readonly WebApplication _app;

    private RequestPipelineHarness(WebApplication app, CapturingSink logs, AlertSink alerts, HttpClient client)
    {
        _app = app;
        Logs = logs;
        Alerts = alerts;
        Client = client;
    }

    public CapturingSink Logs { get; }

    public AlertSink Alerts { get; }

    public HttpClient Client { get; }

    /// <summary>
    /// Starts the harness on the in-memory test server, or with <paramref name="kestrel"/> on a real
    /// Kestrel listening on a free loopback port, whose own error logging is then real too.
    /// </summary>
    public static async Task<RequestPipelineHarness> StartAsync(bool kestrel = false)
    {
        var logs = new CapturingSink();
        var alerts = new AlertSink(new FakeTimeProvider(), TimeSpan.FromMinutes(5));
        var builder = WebApplication.CreateBuilder();
        if (kestrel)
        {
            builder.WebHost.UseUrls("http://127.0.0.1:0");
        }
        else
        {
            builder.WebHost.UseTestServer();
        }

        // preserveStaticLogger: a logger of its own; the static one is shared by parallel tests.
        builder.Services.AddSerilog((_, configuration) => LoggingSetup.Configure(configuration, "Information", logs, alerts), preserveStaticLogger: true);
        var app = builder.Build();
        app.UseRequestLogging();
        app.MapGet("/boom", () =>
        {
            throw new InvalidOperationException("boom failed with " + BotToken);
        });
        app.MapGet("/late-boom", async (HttpContext context) =>
        {
            await context.Response.WriteAsync("partial", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            throw new InvalidOperationException("failed after the response started");
        });
        app.MapGet("/aborted", (HttpContext context) =>
        {
            context.Abort();
            context.RequestAborted.ThrowIfCancellationRequested();
            return Results.Ok();
        });
        app.MapGet("/items/{id}", (int id, ILogger<RequestPipelineHarness> logger) =>
        {
            ItemRead(logger, id, null);
            return Results.Ok(new { id });
        });
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = kestrel
            ? new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()) }
            : app.GetTestClient();
        return new RequestPipelineHarness(app, logs, alerts, client);
    }

    /// <summary>Stops the server, waiting for every request in flight and the server's own logging.</summary>
    public Task StopAsync() => _app.StopAsync(TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.DisposeAsync();
    }
}
