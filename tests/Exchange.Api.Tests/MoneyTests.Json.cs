using System.Text.Json;
using Exchange.Api.Json;
using Shouldly;

namespace Exchange.Api.Tests;

/// <summary>INV-14 and D-007 in JSON: money is written as a string at its scale and read only from a plain decimal string.</summary>
public sealed partial class MoneyTests
{
    private static readonly JsonSerializerOptions Json = CreateJsonOptions();

    private sealed record Holder(decimal MarkupPercent);

    [Theory]
    [InlineData("0.5000", "{\"markup_percent\":\"0.5000\"}")]
    [InlineData("1000.00", "{\"markup_percent\":\"1000.00\"}")]
    [InlineData("0.012345678912", "{\"markup_percent\":\"0.012345678912\"}")]
    [InlineData("9999999999999999.99", "{\"markup_percent\":\"9999999999999999.99\"}")]
    public void Money_is_written_as_a_string_with_its_scale(string value, string expected) =>
        JsonSerializer.Serialize(new Holder(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture)), Json).ShouldBe(expected);

    [Theory]
    [InlineData("{\"markup_percent\":\"100\"}", "100")]
    [InlineData("{\"markup_percent\":\"100.50\"}", "100.50")]
    [InlineData("{\"markup_percent\":\"0.000000000001\"}", "0.000000000001")]
    public void Money_is_read_exactly_from_a_plain_decimal_string(string json, string expected)
    {
        var value = JsonSerializer.Deserialize<Holder>(json, Json)!.MarkupPercent;

        value.ToString(System.Globalization.CultureInfo.InvariantCulture).ShouldBe(expected);
    }

    [Theory]
    [InlineData("{\"markup_percent\":100}")] // a JSON number (D-007)
    [InlineData("{\"markup_percent\":0.5}")]
    [InlineData("{\"markup_percent\":\"1e2\"}")]
    [InlineData("{\"markup_percent\":\"1,5\"}")]
    [InlineData("{\"markup_percent\":\" 1.5\"}")]
    [InlineData("{\"markup_percent\":\"0x10\"}")]
    [InlineData("{\"markup_percent\":\"\"}")]
    [InlineData("{\"markup_percent\":\"NaN\"}")]
    [InlineData("{\"markup_percent\":null}")]
    [InlineData("{\"markup_percent\":true}")]
    [InlineData("{\"markup_percent\":\"1.5\\n\"}")] // a trailing newline
    [InlineData("{\"markup_percent\":\"99999999999999999999999999999999\"}")] // more than a decimal holds
    [InlineData("{\"markup_percent\":\"1.00000000000000000000000000000001\"}")] // would be rounded
    [InlineData("{\"markup_percent\":\"007\"}")] // not the canonical spelling
    [InlineData("{\"markup_percent\":\"-0\"}")]
    public void Anything_but_a_plain_decimal_string_is_refused(string json) =>
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<Holder>(json, Json));

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions();
        ApiJson.Configure(options);
        return options;
    }
}
