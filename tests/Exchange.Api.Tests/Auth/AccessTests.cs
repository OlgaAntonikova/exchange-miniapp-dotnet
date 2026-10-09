using System.Net;
using Exchange.Api.Tests.Data;
using Exchange.Api.Tests.Logging;
using Exchange.Api.Tests.Settings;
using Microsoft.Extensions.Time.Testing;
using Serilog.Events;
using Shouldly;

namespace Exchange.Api.Tests.Auth;

/// <summary>
/// INV-3 for <c>/api/users</c> (the order routes join with PHASE-4): without a valid JWT the answer
/// is the 401 of INV-21, whatever is wrong with the token - none, not Bearer, another key, another
/// algorithm, no signature, another audience, expired, or a user who does not exist. The reason is
/// logged at WARNING; the token is not.
/// </summary>
public sealed class AccessTests(PostgresFixture database)
{
    private const string Me = "/api/users/me";
    private const string Audience = "exchange-miniapp-dotnet";
    private const long TelegramId = 7_000_002;
    private const int TtlSeconds = 600;

    // Long enough for HMAC-SHA512 too (IdentityModel wants a key of at least the hash's size), so
    // an HS512 token is refused for its algorithm, not for its key.
    private static readonly string LongJwtSecret = new('j', 64);

    private static readonly DateTimeOffset Start = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    public static TheoryData<string> Forgeries() => ["other_key", "hs512", "none", "other_audience", "no_exp", "unknown_user", "sub_not_a_number"];

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bearer")]
    [InlineData("Bearer not-a-token")]
    [InlineData("Basic dXNlcjpwYXNz")]
    public async Task A_missing_or_unreadable_token_is_401(string? authorization)
    {
        using var factory = new TestAppFactory(await EnvironmentAsync());
        using var client = factory.CreateClient();

        using var response = await TelegramAuth.GetAsync(client, Me, authorization);

        await AuthApiTests.ShouldBeUnauthorizedAsync(response);
    }

    [Theory]
    [MemberData(nameof(Forgeries))]
    public async Task A_forged_token_is_401(string kind)
    {
        var time = new FakeTimeProvider(Start);
        using var factory = new TestAppFactory(await EnvironmentAsync(), time: time);
        using var client = factory.CreateClient();
        await TelegramAuth.SignInAsync(client, InitData());
        var now = Start.ToUnixTimeSeconds();
        var header = new Dictionary<string, string> { ["alg"] = "HS256", ["typ"] = "JWT" };
        var claims = new Dictionary<string, object> { ["sub"] = TelegramId.ToString(System.Globalization.CultureInfo.InvariantCulture), ["iat"] = now, ["exp"] = now + TtlSeconds, ["aud"] = Audience };
        var token = kind switch
        {
            "other_key" => TelegramAuth.Forge(header, claims, new string('k', 40)),
            "hs512" => TelegramAuth.Forge(new Dictionary<string, string> { ["alg"] = "HS512", ["typ"] = "JWT" }, claims, LongJwtSecret, sha512: true),
            "none" => TelegramAuth.Forge(new Dictionary<string, string> { ["alg"] = "none", ["typ"] = "JWT" }, claims, null),
            "other_audience" => TelegramAuth.Forge(header, With(claims, "aud", "someone-else"), LongJwtSecret),
            "no_exp" => TelegramAuth.Forge(header, Without(claims, "exp"), LongJwtSecret),
            "unknown_user" => TelegramAuth.Forge(header, With(claims, "sub", "999"), LongJwtSecret),
            _ => TelegramAuth.Forge(header, With(claims, "sub", "ivan"), LongJwtSecret),
        };

        using var response = await TelegramAuth.GetAsync(client, Me, "Bearer " + token);

        await AuthApiTests.ShouldBeUnauthorizedAsync(response);
    }

    [Fact]
    public async Task A_token_forged_the_same_way_with_the_right_key_is_accepted()
    {
        // The control for the forgeries above: the same claims under the real key pass, so each
        // forgery is refused for the one thing it changes.
        var time = new FakeTimeProvider(Start);
        using var factory = new TestAppFactory(await EnvironmentAsync(), time: time);
        using var client = factory.CreateClient();
        await TelegramAuth.SignInAsync(client, InitData());
        var now = Start.ToUnixTimeSeconds();
        var token = TelegramAuth.Forge(
            new Dictionary<string, string> { ["alg"] = "HS256", ["typ"] = "JWT" },
            new Dictionary<string, object> { ["sub"] = "7000002", ["iat"] = now, ["exp"] = now + TtlSeconds, ["aud"] = Audience },
            LongJwtSecret);

        using var response = await TelegramAuth.GetAsync(client, Me, "Bearer " + token);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(TtlSeconds - 1, true)]
    [InlineData(TtlSeconds, false)]
    public async Task A_token_is_refused_from_its_expiry_on(int elapsedSeconds, bool accepted)
    {
        var time = new FakeTimeProvider(Start);
        var environment = await EnvironmentAsync();
        environment["JWT_TTL_SECONDS"] = TtlSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var factory = new TestAppFactory(environment, time: time);
        using var client = factory.CreateClient();
        var token = await TelegramAuth.SignInAsync(client, InitData());

        time.Advance(TimeSpan.FromSeconds(elapsedSeconds));
        using var response = await TelegramAuth.GetAsync(client, Me, "Bearer " + token);

        if (accepted)
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        else
        {
            await AuthApiTests.ShouldBeUnauthorizedAsync(response);
        }
    }

    [Fact]
    public async Task A_request_without_a_token_is_refused_by_the_authorization_requirement()
    {
        var logs = new CapturingSink();
        using var factory = new TestAppFactory(await EnvironmentAsync(), logs);
        using var client = factory.CreateClient();

        using var response = await TelegramAuth.GetAsync(client, Me, null);

        await AuthApiTests.ShouldBeUnauthorizedAsync(response);
        AuthApiTests.RefusalReason(logs).ShouldBe("no_token");
    }

    [Fact]
    public async Task A_refusal_logs_its_reason_at_warning_and_never_the_token()
    {
        var logs = new CapturingSink();
        using var factory = new TestAppFactory(await EnvironmentAsync(), logs);
        using var client = factory.CreateClient();
        var forged = TelegramAuth.Forge(
            new Dictionary<string, string> { ["alg"] = "HS256", ["typ"] = "JWT" },
            new Dictionary<string, object> { ["sub"] = "1", ["exp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + TtlSeconds, ["aud"] = Audience },
            new string('k', 40));

        using var response = await TelegramAuth.GetAsync(client, Me, "Bearer " + forged);

        await AuthApiTests.ShouldBeUnauthorizedAsync(response);
        var warning = logs.Events.Where(e => e.Level == LogEventLevel.Warning).ShouldHaveSingleItem();
        warning.Properties.ShouldContainKey("Reason");
        logs.Text.ShouldNotContain(forged.Split('.')[2]);
    }

    private static Dictionary<string, object> With(Dictionary<string, object> claims, string name, object value) =>
        new(claims) { [name] = value };

    private static Dictionary<string, object> Without(Dictionary<string, object> claims, string name)
    {
        var copy = new Dictionary<string, object>(claims);
        copy.Remove(name);
        return copy;
    }

    private static string InitData() => TelegramAuth.InitData(TestSettings.BotToken, TelegramId, "Ann", null, Start.AddSeconds(-5));

    private async Task<Dictionary<string, string?>> EnvironmentAsync()
    {
        var environment = TestSettings.Production();
        environment["DATABASE_URL"] = await database.CreateMigratedDatabaseUrlAsync();
        environment["JWT_SECRET"] = LongJwtSecret;
        return environment;
    }
}
