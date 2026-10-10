using System.Text.Json;
using Exchange.Api.Locale;
using Exchange.Api.Tests.Repository;
using Shouldly;

namespace Exchange.Api.Tests.Locale;

/// <summary>
/// Spec 1.7: the server reads the page's own locale file; a missing key or placeholder is a startup
/// or test failure, never an empty string.
/// </summary>
public sealed class LocaleTextsTests
{
    [Fact]
    public void A_text_is_returned_by_its_key()
    {
        var texts = LocaleTexts.Load("""{"greeting": "Hello", "farewell": "Bye"}""", ["greeting"]);

        texts.Text("greeting").ShouldBe("Hello");
        texts.Text("farewell").ShouldBe("Bye");
    }

    [Fact]
    public void Placeholders_are_filled_by_name()
    {
        var texts = LocaleTexts.Load("""{"status": "Order {order_id} is {status}, order {order_id}"}""", []);

        texts.Text("status", new Dictionary<string, string> { ["order_id"] = "7", ["status"] = "CONFIRMED" })
            .ShouldBe("Order 7 is CONFIRMED, order 7");
    }

    [Fact]
    public void A_filled_value_is_not_read_as_a_placeholder()
    {
        var texts = LocaleTexts.Load("""{"echo": "You wrote {text}"}""", []);

        texts.Text("echo", new Dictionary<string, string> { ["text"] = "{text}" }).ShouldBe("You wrote {text}");
    }

    [Fact]
    public void A_required_key_missing_from_the_file_fails_the_load_naming_the_key()
    {
        var error = Should.Throw<LocaleException>(() => LocaleTexts.Load("""{"greeting": "Hello"}""", ["greeting", "rates_unavailable"]));

        error.Message.ShouldContain("rates_unavailable");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"greeting": 5}""")]
    [InlineData("""{"greeting": null}""")]
    [InlineData("""{"greeting": ""}""")]
    [InlineData("""{"greeting": "a", "greeting": "b"}""")]
    public void A_malformed_file_fails_the_load(string json) =>
        Should.Throw<LocaleException>(() => LocaleTexts.Load(json, []));

    [Fact]
    public void An_unknown_key_fails_instead_of_returning_an_empty_string()
    {
        var texts = LocaleTexts.Load("""{"greeting": "Hello"}""", []);

        Should.Throw<LocaleException>(() => texts.Text("missing")).Message.ShouldContain("missing");
    }

    [Theory]
    [InlineData("""{"t": "Order {order_id}"}""", "")] // a placeholder without a value
    [InlineData("""{"t": "Order"}""", "order_id")] // a value without a placeholder
    [InlineData("""{"t": "Order {order_id}"}""", "orderid")]
    public void Placeholders_and_values_must_match(string json, string valueName)
    {
        var texts = LocaleTexts.Load(json, []);
        var values = valueName.Length == 0 ? new Dictionary<string, string>() : new Dictionary<string, string> { [valueName] = "7" };

        Should.Throw<LocaleException>(() => texts.Text("t", values));
    }

    [Fact]
    public void The_shipped_file_has_every_key_the_server_reads()
    {
        var json = File.ReadAllText(Path.Combine(RepositoryRoot.Path, "src", "Exchange.Api", "wwwroot", "locale", "ru.json"));

        var texts = LocaleTexts.Load(json, LocaleKeys.All);

        // Each key the server reads takes exactly these placeholders; any other key takes none.
        var placeholders = new Dictionary<string, string[]>
        {
            [LocaleKeys.OperatorNewOrder] = ["order_id"],
            [LocaleKeys.OperatorOrderStatus] = ["order_id", "status"],
            [LocaleKeys.ClientStatusChanged] = ["order_id", "status"],
        };
        foreach (var key in LocaleKeys.All)
        {
            var values = placeholders.GetValueOrDefault(key, []).ToDictionary(name => name, name => "x");
            texts.Text(key, values).Length.ShouldBePositive(key);
        }
        using var document = JsonDocument.Parse(json);
        document.RootElement.EnumerateObject().ShouldNotBeEmpty();
    }
}
