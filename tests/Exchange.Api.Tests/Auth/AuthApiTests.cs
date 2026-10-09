using System.Net;
using System.Text;
using System.Text.Json;
using Exchange.Api.Auth;
using Exchange.Api.Settings;
using Exchange.Api.Tests.Data;
using Exchange.Api.Tests.Logging;
using Exchange.Api.Tests.Settings;
using Npgsql;
using Serilog.Events;
using Shouldly;

namespace Exchange.Api.Tests.Auth;

/// <summary>
/// Spec 1.10 and PHASE-2: <c>POST /api/auth/telegram</c> turns a valid initData into a JWT and
/// creates or refreshes the user; <c>GET /api/users/me</c> answers for the token's user. A refused
/// initData is the 401 of INV-21 and changes nothing (INV-1, RULE-SERVER-AUTHORITY).
/// </summary>
public sealed class AuthApiTests(PostgresFixture database)
{
    private const long TelegramId = 7_000_001;
    private const int DefaultTtlSeconds = 3600;
    private const string Audience = "exchange-miniapp-dotnet";
    private const int SignInCapBytes = 16_384; // D-029: twice the 8192-character initData cap

    [Fact]
    public async Task A_valid_init_data_returns_a_bearer_token_for_the_ttl()
    {
        using var factory = new TestAppFactory(await EnvironmentAsync());
        using var client = factory.CreateClient();

        using var response = await TelegramAuth.PostAsync(client, InitData());

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var body = document.RootElement;
        body.EnumerateObject().Select(p => p.Name).ShouldBe(["access_token", "token_type", "expires_in"], ignoreOrder: true);
        body.GetProperty("token_type").GetString().ShouldBe("bearer");
        body.GetProperty("expires_in").GetInt32().ShouldBe(DefaultTtlSeconds);
    }

    [Fact]
    public async Task The_token_is_hs256_with_exactly_sub_iat_exp_and_aud()
    {
        using var factory = new TestAppFactory(await EnvironmentAsync());
        using var client = factory.CreateClient();

        var (header, payload) = TelegramAuth.Decode(await TelegramAuth.SignInAsync(client, InitData()));

        header.GetProperty("alg").GetString().ShouldBe("HS256");
        payload.EnumerateObject().Select(p => p.Name).ShouldBe(["sub", "iat", "exp", "aud"], ignoreOrder: true);
        payload.GetProperty("sub").GetString().ShouldBe("7000001");
        payload.GetProperty("aud").GetString().ShouldBe(Audience);
        (payload.GetProperty("exp").GetInt64() - payload.GetProperty("iat").GetInt64()).ShouldBe(DefaultTtlSeconds);
        payload.GetProperty("iat").GetInt64().ShouldBeInRange(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 1);
    }

    [Fact]
    public async Task The_token_lifetime_follows_jwt_ttl_seconds()
    {
        var environment = await EnvironmentAsync();
        environment["JWT_TTL_SECONDS"] = "120";
        using var factory = new TestAppFactory(environment);
        using var client = factory.CreateClient();

        using var response = await TelegramAuth.PostAsync(client, InitData());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        document.RootElement.GetProperty("expires_in").GetInt32().ShouldBe(120);
        var (_, payload) = TelegramAuth.Decode(document.RootElement.GetProperty("access_token").GetString()!);
        (payload.GetProperty("exp").GetInt64() - payload.GetProperty("iat").GetInt64()).ShouldBe(120);
    }

    [Fact]
    public async Task Me_answers_with_the_signed_in_user()
    {
        using var factory = new TestAppFactory(await EnvironmentAsync());
        using var client = factory.CreateClient();
        var token = await TelegramAuth.SignInAsync(client, InitData());

        using var response = await TelegramAuth.GetAsync(client, "/api/users/me", "Bearer " + token);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldBe("""{"telegram_id":7000001,"username":"ivan_p","first_name":"Ivan","loyalty_points":0}""");
    }

    [Fact]
    public async Task A_user_without_a_username_has_a_null_username()
    {
        using var factory = new TestAppFactory(await EnvironmentAsync());
        using var client = factory.CreateClient();
        var token = await TelegramAuth.SignInAsync(client, InitData(username: null));

        using var response = await TelegramAuth.GetAsync(client, "/api/users/me", "Bearer " + token);

        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldBe("""{"telegram_id":7000001,"username":null,"first_name":"Ivan","loyalty_points":0}""");
    }

    [Fact]
    public async Task Signing_in_again_refreshes_the_name_and_keeps_one_row()
    {
        var environment = await EnvironmentAsync();
        using var factory = new TestAppFactory(environment);
        using var client = factory.CreateClient();
        await TelegramAuth.SignInAsync(client, InitData());
        await SqlAsync(environment, $"UPDATE users SET loyalty_points = 3 WHERE telegram_id = {TelegramId}");

        var token = await TelegramAuth.SignInAsync(client, InitData(firstName: "Ivan2", username: "ivan_new"));
        using var response = await TelegramAuth.GetAsync(client, "/api/users/me", "Bearer " + token);

        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldBe("""{"telegram_id":7000001,"username":"ivan_new","first_name":"Ivan2","loyalty_points":3}""");
        (await CountUsersAsync(environment)).ShouldBe(1);
    }

    [Theory]
    [InlineData("altered")]
    [InlineData("other_bot")]
    [InlineData("expired")]
    public async Task A_refused_init_data_is_401_and_creates_no_user(string kind)
    {
        var environment = await EnvironmentAsync();
        var logs = new CapturingSink();
        using var factory = new TestAppFactory(environment, logs);
        using var client = factory.CreateClient();
        var initData = kind switch
        {
            "altered" => InitData().Replace("ivan_p", "ivan_q", StringComparison.Ordinal),
            "other_bot" => TelegramAuth.InitData("4242" + ":" + new string('B', 35), TelegramId, "Ivan", "ivan_p", DateTimeOffset.UtcNow),
            _ => InitData(authDate: DateTimeOffset.UtcNow.AddDays(-2)),
        };

        using var response = await TelegramAuth.PostAsync(client, initData);

        await ShouldBeUnauthorizedAsync(response);
        (await CountUsersAsync(environment)).ShouldBe(0);
        var warning = logs.Events.Where(e => e.Level == LogEventLevel.Warning).ShouldHaveSingleItem();
        warning.Properties.ShouldContainKey("Reason");
        logs.Text.ShouldNotContain("ivan_p");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"init_data":5}""")]
    [InlineData("""{"init_data":null}""")]
    public async Task A_body_without_an_init_data_string_is_401(string body)
    {
        using var factory = new TestAppFactory(await EnvironmentAsync());
        using var client = factory.CreateClient();
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await client.PostAsync(new Uri("/api/auth/telegram", UriKind.Relative), content, TestContext.Current.CancellationToken);

        await ShouldBeUnauthorizedAsync(response);
    }

    [Theory]
    [InlineData(SignInCapBytes, "too_long")] // read whole, then refused by the validator
    [InlineData(SignInCapBytes + 1, "body_too_long")]
    [InlineData(1_000_000, "body_too_long")]
    public async Task A_body_over_the_cap_is_refused_as_too_long(int bodyBytes, string reason)
    {
        var logs = new CapturingSink();
        using var factory = new TestAppFactory(await EnvironmentAsync(), logs);
        using var client = factory.CreateClient();
        const string Envelope = "{\"init_data\":\"a=\"}";
        var body = Envelope.Insert(Envelope.Length - 2, new string('a', bodyBytes - Envelope.Length));
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await client.PostAsync(new Uri("/api/auth/telegram", UriKind.Relative), content, TestContext.Current.CancellationToken);

        Encoding.UTF8.GetByteCount(body).ShouldBe(bodyBytes);
        await ShouldBeUnauthorizedAsync(response);
        RefusalReason(logs).ShouldBe(reason);
    }

    /// <summary>The reason of the one WARNING an auth refusal logs.</summary>
    internal static string RefusalReason(CapturingSink logs) =>
        ((ScalarValue)logs.Events.Where(e => e.Level == LogEventLevel.Warning).ShouldHaveSingleItem().Properties["Reason"]).Value!.ToString()!;

    [Fact]
    public async Task A_user_id_in_the_body_is_ignored()
    {
        var environment = await EnvironmentAsync();
        using var factory = new TestAppFactory(environment);
        using var client = factory.CreateClient();
        using var content = new StringContent(
            JsonSerializer.Serialize(new Dictionary<string, object> { ["init_data"] = InitData(), ["telegram_id"] = 1, ["user_id"] = 1 }),
            Encoding.UTF8,
            "application/json");

        using var response = await client.PostAsync(new Uri("/api/auth/telegram", UriKind.Relative), content, TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        TelegramAuth.Decode(document.RootElement.GetProperty("access_token").GetString()!).Payload.GetProperty("sub").GetString().ShouldBe("7000001");
    }

    [Theory]
    [InlineData(10, 10, true)]
    [InlineData(11, 10, false)]
    [InlineData(1_000_000, 10, false)]
    public async Task A_capped_read_takes_at_most_one_byte_past_the_cap(int streamBytes, int capBytes, bool fits)
    {
        await using var stream = new CountingStream(new byte[streamBytes]);

        var body = await AuthEndpoints.ReadCappedAsync(stream, capBytes, TestContext.Current.CancellationToken);

        (body is not null).ShouldBe(fits);
        body?.Length.ShouldBe(streamBytes);
        stream.BytesRead.ShouldBeLessThanOrEqualTo(capBytes + 1);
    }

    /// <summary>The 401 of INV-21: the exact body and the Bearer challenge.</summary>
    internal static async Task ShouldBeUnauthorizedAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().ShouldBe("Bearer");
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBe("""{"error":"unauthorized"}""");
    }

    private static string InitData(string firstName = "Ivan", string? username = "ivan_p", DateTimeOffset? authDate = null) =>
        TelegramAuth.InitData(TestSettings.BotToken, TelegramId, firstName, username, authDate ?? DateTimeOffset.UtcNow.AddSeconds(-5));

    private static async Task SqlAsync(Dictionary<string, string?> environment, string sql)
    {
        await using var connection = new NpgsqlConnection(DatabaseUrl.Convert(environment["DATABASE_URL"]!).ConnectionString!.Reveal());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<long> CountUsersAsync(Dictionary<string, string?> environment)
    {
        await using var connection = new NpgsqlConnection(DatabaseUrl.Convert(environment["DATABASE_URL"]!).ConnectionString!.Reveal());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM users", connection);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private async Task<Dictionary<string, string?>> EnvironmentAsync()
    {
        var environment = TestSettings.Production();
        environment["DATABASE_URL"] = await database.CreateMigratedDatabaseUrlAsync();
        return environment;
    }
}

/// <summary>A memory stream that counts the bytes handed out, in reads of at most 3 bytes.</summary>
internal sealed class CountingStream(byte[] content) : MemoryStream(content)
{
    private const int MaxChunkBytes = 3;

    public long BytesRead { get; private set; }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await base.ReadAsync(buffer[..Math.Min(buffer.Length, MaxChunkBytes)], cancellationToken);
        BytesRead += read;
        return read;
    }
}
