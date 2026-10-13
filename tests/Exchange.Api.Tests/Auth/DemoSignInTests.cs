using Exchange.Api.Tests.Data;
using Exchange.Api.Tests.Logging;
using Exchange.Api.Tests.Settings;
using Shouldly;

namespace Exchange.Api.Tests.Auth;

/// <summary>Spec 1.9: demo mode has no bot, so no initData can sign anyone in there.</summary>
[Collection(nameof(ProcessEnvironmentGroup))]
public sealed class DemoSignInTests(PostgresFixture database)
{
    [Fact]
    public async Task The_bot_webhook_is_not_mapped_in_demo_mode()
    {
        var url = await database.CreateDatabaseUrlAsync();
        TestAppFactory? factory = null;
        HttpClient? client = null;
        ProcessEnvironment.With("demo", () =>
        {
            factory = new TestAppFactory(TestSettings.Demo(url));
            client = factory.CreateClient();
        });

        try
        {
            using var content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
            using var response = await client!.PostAsync(new Uri("/api/bot/webhook", UriKind.Relative), content, TestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(System.Net.HttpStatusCode.NotFound);
        }
        finally
        {
            client?.Dispose();
            await factory!.DisposeAsync();
        }
    }

    [Fact]
    public async Task Telegram_sign_in_is_refused_in_demo_mode()
    {
        var logs = new CapturingSink();
        var url = await database.CreateDatabaseUrlAsync();
        TestAppFactory? factory = null;
        HttpClient? client = null;
        ProcessEnvironment.With("demo", () =>
        {
            factory = new TestAppFactory(TestSettings.Demo(url), logs);
            client = factory.CreateClient(); // the host reads APP_ENV here, once
        });

        try
        {
            using var response = await TelegramAuth.PostAsync(
                client!,
                TelegramAuth.InitData(TestSettings.BotToken, 7_000_004, "Ann", null, DateTimeOffset.UtcNow));

            await AuthApiTests.ShouldBeUnauthorizedAsync(response);
            AuthApiTests.RefusalReason(logs).ShouldBe("telegram_disabled");
        }
        finally
        {
            client?.Dispose();
            await factory!.DisposeAsync();
        }
    }
}
