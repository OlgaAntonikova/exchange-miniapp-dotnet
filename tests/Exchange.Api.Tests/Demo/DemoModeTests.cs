using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Exchange.Api.Data;
using Exchange.Api.Demo;
using Exchange.Api.Tests.Data;
using Exchange.Api.Tests.Logging;
using Exchange.Api.Tests.Rates;
using Exchange.Api.Tests.Settings;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Events;
using Shouldly;

namespace Exchange.Api.Tests.Demo;

/// <summary>
/// Demo mode end to end (spec 1.9). INV-15: <c>POST /api/auth/demo</c> answers only when
/// <c>APP_ENV=demo</c>; anywhere else it is 404 and creates no user. INV-16: demo mode refuses to
/// start beside a production secret or a database password, sends nothing to Telegram, and logs no
/// message text. INV-17 for demo mode: the routes it maps are the table's, the demo row included.
/// </summary>
[Collection(nameof(ProcessEnvironmentGroup))]
public sealed class DemoModeTests(PostgresFixture database)
{
    private const string DemoRoute = "/api/auth/demo";

    // Nothing listens on port 1: a start that got past the settings would fail differently.
    private const string PasswordLessUrl = "postgresql://postgres@127.0.0.1:1/demo";

    // --- INV-15 -----------------------------------------------------------------------------------

    [Fact]
    public async Task The_demo_sign_in_answers_with_a_token_for_the_one_demo_user()
    {
        var logs = new CapturingSink();
        await using var app = await DemoAppAsync(logs);

        using var first = await app.Client.PostAsync(new Uri(DemoRoute, UriKind.Relative), null, TestContext.Current.CancellationToken);
        await ApiContractTests.ShouldMatchRowAsync(first, "POST", DemoRoute);
        using var second = await app.Client.PostAsync(new Uri(DemoRoute, UriKind.Relative), null, TestContext.Current.CancellationToken);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var me = await GetMeAsync(app.Client, await TokenAsync(second));
        me.RootElement.GetProperty("telegram_id").GetInt64().ShouldBe(DemoEndpoints.DemoTelegramId);
        me.RootElement.GetProperty("first_name").GetString().ShouldBe(DemoEndpoints.DemoFirstName);
        me.RootElement.GetProperty("username").ValueKind.ShouldBe(JsonValueKind.Null);
        app.Users().ShouldBe(1); // signing in again is the same user
        logs.Events.Count(e => e.Level == LogEventLevel.Information && e.MessageTemplate.Text == "Signed in to the demo").ShouldBe(2);
    }

    [Theory]
    [InlineData("test")]
    [InlineData("production")]
    [InlineData("development")]
    public async Task Outside_demo_mode_the_demo_sign_in_is_absent_and_creates_nobody(string appEnv)
    {
        var environment = TestSettings.Production();
        environment["DATABASE_URL"] = await database.CreateMigratedDatabaseUrlAsync();
        await using var app = Host(appEnv, environment);

        using var response = await app.Client.PostAsync(new Uri(DemoRoute, UriKind.Relative), null, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        app.Users().ShouldBe(0);
    }

    // --- INV-17 in demo mode ------------------------------------------------------------------------

    [Fact]
    public async Task Demo_mode_maps_the_table_rows_served_there_and_no_other_route()
    {
        await using var app = await DemoAppAsync();

        var mapped = ApiContractTests.MappedRoutes(app.Factory.Services.GetRequiredService<EndpointDataSource>());

        mapped.ShouldBe(
            ApiContractTests.Table.Where(row => row.Served != ApiContractTests.Served.OutsideDemo).Select(row => (row.Method, row.Route)).Concat(ApiContractTests.PageRoutes),
            ignoreOrder: true);
    }

    // --- INV-16 -----------------------------------------------------------------------------------

    // The real process, as docker compose would start it: one FATAL line naming the variable, never
    // its value, and exit code 1 (D-020). The refusal comes before any database connection.
    [Theory]
    [InlineData("BOT_TOKEN", "123456:" + "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghi")]
    [InlineData("BOT_WEBHOOK_SECRET", "webhook-value-x")]
    [InlineData("JWT_SECRET", "jwt-value-0123456789abcdef0123456789")]
    [InlineData("PII_ENCRYPTION_KEY", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("PGPASSWORD", "pg-value-x")]
    public async Task Demo_mode_refuses_to_start_beside_a_production_secret(string name, string value)
    {
        var (exitCode, output) = await StartupFailureTests.RunAppAsync(new()
        {
            ["APP_ENV"] = "demo",
            ["DATABASE_URL"] = PasswordLessUrl,
            [name] = value,
        });

        exitCode.ShouldBe(StartupFailureTests.ExpectedExitCode);
        output.ShouldContain($"{name} must not be set in demo mode");
        output.ShouldNotContain(value);
    }

    [Fact]
    public async Task Demo_mode_refuses_to_start_on_a_database_url_with_a_password()
    {
        var (exitCode, output) = await StartupFailureTests.RunAppAsync(new()
        {
            ["APP_ENV"] = "demo",
            ["DATABASE_URL"] = "postgresql://postgres:" + "db-value-x" + "@127.0.0.1:1/demo",
        });

        exitCode.ShouldBe(StartupFailureTests.ExpectedExitCode);
        output.ShouldContain("DATABASE_URL must carry no password in demo mode");
        output.ShouldNotContain("db-value-x");
    }

    [Fact]
    public async Task A_demo_order_and_its_cancel_send_nothing_to_telegram_and_log_no_message_text()
    {
        var logs = new CapturingSink();
        var telegram = new CountingHandler();
        await using var app = await DemoAppAsync(logs, telegram);
        using var signIn = await app.Client.PostAsync(new Uri(DemoRoute, UriKind.Relative), null, TestContext.Current.CancellationToken);
        var token = await TokenAsync(signIn);

        using var placed = await SendAsync(app.Client, HttpMethod.Post, "/api/orders", token, JsonContent.Create(new Dictionary<string, string>
        {
            ["give"] = "USD_CASH",
            ["receive"] = "EUR_WIRE",
            ["amount_give"] = "100",
            ["iban"] = "ME25505000012345678951",
            ["recipient_name"] = "Ivan Petrov",
            ["payment_purpose"] = "Rent for October",
        }));
        placed.StatusCode.ShouldBe(HttpStatusCode.Created);
        var orderId = JsonDocument.Parse(await placed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.GetProperty("id").GetInt32();
        using var cancelled = await SendAsync(app.Client, HttpMethod.Post, $"/api/orders/{orderId}/cancel", token, null);
        cancelled.StatusCode.ShouldBe(HttpStatusCode.OK);

        // The operator's new-order message, then the client's and the operator's status messages,
        // each replaced by one INFO record naming its kind and the order.
        await WaitForAsync(() => Suppressed(logs, orderId).Count >= 3);
        Suppressed(logs, orderId).ShouldBe(["new order", "client status", "operator status"], ignoreOrder: true);
        // With today's wiring demo mode builds no Bot API client at all, so this guards against one
        // being built later; the suppressed records above are what shows nothing was sent.
        telegram.Requests.ShouldBe(0);

        // The formatted output, every property included, as a sink writes it.
        var text = logs.Text;
        text.ShouldNotContain("Ivan Petrov");
        text.ShouldNotContain("Rent for October");
        text.ShouldNotContain("5678951");
    }

    // The operations of the suppressed messages for the order, in log order.
    private static List<string> Suppressed(CapturingSink logs, int orderId) =>
        [.. logs.Events
            .Where(e => e.Level == LogEventLevel.Information
                && e.MessageTemplate.Text.StartsWith("Notification suppressed in demo mode", StringComparison.Ordinal)
                && e.Properties["OrderId"].ToString() == orderId.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Select(e => ((ScalarValue)e.Properties["Operation"]).Value!.ToString()!)];

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    private async Task<HostedApp> DemoAppAsync(CapturingSink? logs = null, CountingHandler? telegram = null) =>
        Host("demo", TestSettings.Demo(await database.CreateDatabaseUrlAsync()), logs, telegram);

    // The app with APP_ENV set to appEnv while it is built: the host reads it then, once.
    private static HostedApp Host(string appEnv, Dictionary<string, string?> environment, CapturingSink? logs = null, CountingHandler? telegram = null)
    {
        HostedApp? app = null;
        ProcessEnvironment.With(appEnv, () =>
        {
            var factory = new TestAppFactory(environment, logs, services: services =>
            {
                FakeSources.Replace()(services);
                if (telegram is not null)
                {
                    services.AddKeyedSingleton(Exchange.Api.Bot.BotSetup.ClientKey, (_, _) => new HttpClient(telegram));
                    services.AddKeyedSingleton(Exchange.Api.Alerts.TelegramAlertSetup.ClientKey, (_, _) => new HttpClient(telegram));
                }
            });
            try
            {
                app = new HostedApp(factory, factory.CreateClient());
            }
            catch
            {
                factory.Dispose();
                throw;
            }
        });
        return app!;
    }

    private static async Task<string> TokenAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return document.RootElement.GetProperty("access_token").GetString()!;
    }

    private static async Task<JsonDocument> GetMeAsync(HttpClient client, string token)
    {
        using var response = await SendAsync(client, HttpMethod.Get, "/api/users/me", token, null);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string token, HttpContent? content)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private sealed class HostedApp(TestAppFactory factory, HttpClient client) : IAsyncDisposable, IDisposable
    {
        public TestAppFactory Factory { get; } = factory;

        public HttpClient Client { get; } = client;

        public int Users()
        {
            using var scope = Factory.Services.CreateScope();
            return scope.ServiceProvider.GetRequiredService<ExchangeDbContext>().Users.Count();
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Factory.DisposeAsync();
        }

        public void Dispose()
        {
            Client.Dispose();
            Factory.Dispose();
        }
    }

    // Counts every request that would have left for Telegram, and refuses it.
    private sealed class CountingHandler : HttpMessageHandler
    {
        private int _requests;

        public int Requests => Volatile.Read(ref _requests);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            return Task.FromException<HttpResponseMessage>(new HttpRequestException("no network in tests"));
        }
    }
}
