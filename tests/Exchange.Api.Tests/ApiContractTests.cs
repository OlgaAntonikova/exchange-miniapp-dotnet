using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Exchange.Api.Tests.Auth;
using Exchange.Api.Tests.Data;
using Exchange.Api.Tests.Rates;
using Exchange.Api.Tests.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Exchange.Api.Tests;

/// <summary>
/// INV-17: every row of the API table of spec 1.10 is served with its method, path, auth
/// requirement, success status and response property names and JSON types. Rows not yet built
/// are listed as pending, and the list can only shrink: a pending route must not be mapped, a
/// mapped route must be in the table, and the list is empty at the PHASE-5 gate.
/// </summary>
public sealed class ApiContractTests(PostgresFixture database)
{
    private static readonly Dictionary<string, JsonValueKind> HealthShape = new() { ["status"] = JsonValueKind.String };

    private static readonly Dictionary<string, JsonValueKind> CityShape = new()
    {
        ["id"] = JsonValueKind.Number,
        ["name"] = JsonValueKind.String,
        ["markup_percent"] = JsonValueKind.String,
    };

    private static readonly Dictionary<string, JsonValueKind> BankShape = new()
    {
        ["id"] = JsonValueKind.Number,
        ["code"] = JsonValueKind.String,
        ["channel"] = JsonValueKind.String,
        ["currency"] = JsonValueKind.String,
        ["country"] = JsonValueKind.String,
        ["markup_percent"] = JsonValueKind.String,
    };

    private static readonly Dictionary<string, JsonValueKind> NetworkShape = new()
    {
        ["id"] = JsonValueKind.Number,
        ["code"] = JsonValueKind.String,
        ["fee_percent"] = JsonValueKind.String,
    };

    private static readonly Dictionary<string, JsonValueKind> DirectionShape = new()
    {
        ["give"] = JsonValueKind.String,
        ["receive"] = JsonValueKind.String,
        ["quote_fields"] = JsonValueKind.Array,
        ["order_fields"] = JsonValueKind.Array,
        ["optional_fields"] = JsonValueKind.Array,
    };

    private static readonly Dictionary<string, JsonValueKind> TokenShape = new()
    {
        ["access_token"] = JsonValueKind.String,
        ["token_type"] = JsonValueKind.String,
        ["expires_in"] = JsonValueKind.Number,
    };

    private static readonly Dictionary<string, JsonValueKind> MeShape = new()
    {
        ["telegram_id"] = JsonValueKind.Number,
        ["username"] = JsonValueKind.String,
        ["first_name"] = JsonValueKind.String,
        ["loyalty_points"] = JsonValueKind.Number,
    };

    private static readonly Dictionary<string, JsonValueKind> QuoteShape = new()
    {
        ["give"] = JsonValueKind.String,
        ["receive"] = JsonValueKind.String,
        ["amount_give"] = JsonValueKind.String,
        ["amount_receive"] = JsonValueKind.String,
        ["base_rate"] = JsonValueKind.String,
        ["effective_rate"] = JsonValueKind.String,
        ["markup_percent"] = JsonValueKind.Object,
        ["provider"] = JsonValueKind.String,
        ["rate_taken_at"] = JsonValueKind.String,
    };

    private static readonly Dictionary<string, JsonValueKind> OrderShape = new()
    {
        ["id"] = JsonValueKind.Number,
        ["status"] = JsonValueKind.String,
        ["give"] = JsonValueKind.String,
        ["receive"] = JsonValueKind.String,
        ["amount_give"] = JsonValueKind.String,
        ["amount_receive"] = JsonValueKind.String,
        ["base_rate"] = JsonValueKind.String,
        ["effective_rate"] = JsonValueKind.String,
        ["markup_percent"] = JsonValueKind.Object,
        ["provider"] = JsonValueKind.String,
        ["rate_taken_at"] = JsonValueKind.String,
        ["created_at"] = JsonValueKind.String,
        ["closed_at"] = JsonValueKind.Null, // a new order is open
        ["details"] = JsonValueKind.Object,
    };

    // Spec 1.10, row by row: method, route template, the example path requested, whether it needs a
    // token, success status, whether the body is a list, and the shape of one object (null: not
    // built yet).
    private static readonly List<(string Method, string Route, string Path, bool Auth, HttpStatusCode Status, bool IsList, Dictionary<string, JsonValueKind>? Shape)> Table =
    [
        ("GET", "/healthz", "/healthz", false, HttpStatusCode.OK, false, HealthShape),
        ("GET", "/api/cities", "/api/cities", false, HttpStatusCode.OK, true, CityShape),
        ("GET", "/api/banks", "/api/banks", false, HttpStatusCode.OK, true, BankShape),
        ("GET", "/api/usdt-networks", "/api/usdt-networks", false, HttpStatusCode.OK, true, NetworkShape),
        ("GET", "/api/directions", "/api/directions", false, HttpStatusCode.OK, true, DirectionShape),
        ("POST", "/api/auth/telegram", "/api/auth/telegram", false, HttpStatusCode.OK, false, TokenShape),
        ("POST", "/api/auth/demo", "/api/auth/demo", false, HttpStatusCode.OK, false, null),
        ("GET", "/api/users/me", "/api/users/me", true, HttpStatusCode.OK, false, MeShape),
        ("POST", "/api/rate", "/api/rate", true, HttpStatusCode.OK, false, QuoteShape),
        ("POST", "/api/orders", "/api/orders", true, HttpStatusCode.Created, false, OrderShape),
        ("GET", "/api/orders", "/api/orders", true, HttpStatusCode.OK, true, OrderShape),
        ("GET", "/api/orders/{order_id}", "/api/orders/1", true, HttpStatusCode.OK, false, OrderShape),
        ("POST", "/api/orders/{order_id}/cancel", "/api/orders/1/cancel", true, HttpStatusCode.OK, false, null),
        ("POST", "/api/bot/webhook", "/api/bot/webhook", false, HttpStatusCode.OK, false, null),
    ];

    public static TheoryData<string, string> Built() =>
        [.. Table.Where(row => row.Shape is not null).Select(row => (row.Method, row.Route))];

    public static TheoryData<string, string> BuiltWithAuth() =>
        [.. Table.Where(row => row.Shape is not null && row.Auth).Select(row => (row.Method, row.Route))];

    public static TheoryData<string, string> Pending() =>
        [.. Table.Where(row => row.Shape is null).Select(row => (row.Method, row.Route))];

    [Theory]
    [MemberData(nameof(Built))]
    public async Task A_built_row_answers_with_its_status_and_shape(string method, string route)
    {
        var row = Table.Single(r => r.Method == method && r.Route == route);
        using var factory = new TestAppFactory(await EnvironmentAsync(), services: FakeSources.Replace());
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), row.Path);
        if (route == SignInRoute)
        {
            request.Content = JsonContent.Create(new Dictionary<string, string> { ["init_data"] = InitData() });
        }

        if (route == RateRoute)
        {
            request.Content = JsonContent.Create(new Dictionary<string, string> { ["give"] = "USD_CASH", ["receive"] = "EUR_WIRE", ["amount_give"] = "100" });
        }

        if (route == OrdersRoute && method == "POST")
        {
            request.Content = OrderBody();
        }

        if (row.Auth)
        {
            var token = await TelegramAuth.SignInAsync(client, InitData());
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (route.StartsWith(OrdersRoute, StringComparison.Ordinal) && method == "GET")
            {
                // The caller's first order, which is order 1 in a fresh database.
                using var create = new HttpRequestMessage(HttpMethod.Post, OrdersRoute) { Content = OrderBody() };
                create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var created = await client.SendAsync(create, TestContext.Current.CancellationToken);
                created.StatusCode.ShouldBe(HttpStatusCode.Created);
            }
        }

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(row.Status);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var objects = row.IsList ? [.. document.RootElement.EnumerateArray()] : new List<JsonElement> { document.RootElement };
        objects.ShouldNotBeEmpty();
        foreach (var item in objects)
        {
            item.ValueKind.ShouldBe(JsonValueKind.Object);
            item.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind).ShouldBe(row.Shape!, ignoreOrder: true);
            foreach (var list in item.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Array))
            {
                list.Value.EnumerateArray().ShouldAllBe(element => element.ValueKind == JsonValueKind.String, list.Name); // field-name lists
            }
        }
    }

    [Theory]
    [MemberData(nameof(BuiltWithAuth))]
    public async Task A_built_row_that_needs_a_token_refuses_a_request_without_one(string method, string route)
    {
        var row = Table.Single(r => r.Method == method && r.Route == route);
        using var factory = new TestAppFactory(await EnvironmentAsync(), services: FakeSources.Replace());
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), row.Path), TestContext.Current.CancellationToken);

        await AuthApiTests.ShouldBeUnauthorizedAsync(response);
    }

    [Theory]
    [MemberData(nameof(Pending))]
    public async Task A_pending_row_is_not_mapped_yet(string method, string route)
    {
        var row = Table.Single(r => r.Method == method && r.Route == route);
        using var factory = new TestAppFactory(await EnvironmentAsync(), services: FakeSources.Replace());
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), row.Path), TestContext.Current.CancellationToken);

        // Once a pending route answers, it has been built: move it to the built rows with its shape.
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, $"{method} {route} is mapped; give it its shape in the table");
    }

    [Fact]
    public async Task Every_mapped_route_is_a_built_row_of_the_table()
    {
        using var factory = new TestAppFactory(await EnvironmentAsync(), services: FakeSources.Replace());
        using var client = factory.CreateClient();

        var mapped = MappedRoutes(factory.Services.GetRequiredService<EndpointDataSource>());
        var built = Table.Where(row => row.Shape is not null).Select(row => (row.Method, row.Route)).ToHashSet();

        mapped.ShouldBe(built, ignoreOrder: true);
    }

    [Fact]
    public async Task A_route_without_a_method_is_counted_as_every_method()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        await using var app = builder.Build();
        app.Map("/anything", () => "ok");
        app.MapGet("/one", () => "ok");
        await app.StartAsync(TestContext.Current.CancellationToken);

        MappedRoutes(app.Services.GetRequiredService<EndpointDataSource>()).ShouldBe([("*", "/anything"), ("GET", "/one")], ignoreOrder: true);
    }

    // Every (method, route) the app maps. A route without method metadata (app.Map, a fallback)
    // answers every method: "*" can match no row of the table, so it fails the comparison instead
    // of disappearing.
    private static HashSet<(string Method, string Route)> MappedRoutes(EndpointDataSource source) =>
        source.Endpoints.OfType<RouteEndpoint>()
            .SelectMany(endpoint => (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"])
                .Select(method => (method, "/" + endpoint.RoutePattern.RawText!.TrimStart('/'))))
            .ToHashSet();

    private const string SignInRoute = "/api/auth/telegram";
    private const string RateRoute = "/api/rate";
    private const string OrdersRoute = "/api/orders";

    private static JsonContent OrderBody() => JsonContent.Create(new Dictionary<string, string>
    {
        ["give"] = "USD_CASH",
        ["receive"] = "EUR_WIRE",
        ["amount_give"] = "100",
        ["iban"] = "ME25505000012345678951",
        ["recipient_name"] = "Ivan Petrov",
    });

    private static string InitData() =>
        TelegramAuth.InitData(TestSettings.BotToken, 7_000_003, "Ivan", "ivan_p", DateTimeOffset.UtcNow.AddSeconds(-5));

    private async Task<Dictionary<string, string?>> EnvironmentAsync()
    {
        var environment = TestSettings.Production();
        environment["DATABASE_URL"] = await database.CreateMigratedDatabaseUrlAsync();
        return environment;
    }
}
