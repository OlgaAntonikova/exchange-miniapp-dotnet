using System.Text.Json;
using Exchange.Api.Pricing;
using Exchange.Domain.Directions;
using Shouldly;

namespace Exchange.Api.Tests.Pricing;

/// <summary>
/// Spec 1.10, request rules: codes in upper case, the amount a JSON string of at most 16 integer
/// digits and 2 decimals above zero, ids JSON integers from 1 to 2147483647; every bad field is
/// listed, <c>body.</c>-prefixed, and properties outside the contract are ignored.
/// </summary>
public sealed class PricingRequestTests
{
    [Fact]
    public void A_valid_request_parses_into_domain_values()
    {
        var input = Parse("""{"give": "RUB_WIRE", "receive": "EUR_ATM", "amount_give": "10000.50", "bank_give_id": 1, "bank_receive_id": 5}""");

        input.Give.ShouldBe(GiveCode.RubWire);
        input.Receive.ShouldBe(ReceiveCode.EurAtm);
        input.AmountGive.ShouldBe(10000.50m);
        input.ReferenceIds.ShouldBe(new Dictionary<string, int> { ["bank_give_id"] = 1, ["bank_receive_id"] = 5 }, ignoreOrder: true);
    }

    [Fact]
    public void A_null_id_counts_as_absent()
    {
        var input = Parse("""{"give": "USD_CASH", "receive": "EUR_WIRE", "amount_give": "100", "city_id": null}""");

        input.ReferenceIds.ShouldBeEmpty();
    }

    [Fact]
    public void Properties_outside_the_contract_are_ignored()
    {
        var input = Parse("""{"give": "USD_CASH", "receive": "EUR_WIRE", "amount_give": "100", "amount_receive": "1", "rate": "999", "user_id": 5, "Give": "EUR_CASH", "iban": "x"}""");

        input.Give.ShouldBe(GiveCode.UsdCash);
        input.ReferenceIds.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("0.01")]
    [InlineData("1")]
    [InlineData("1.5")]
    [InlineData("9999999999999999.99")] // 16 integer digits
    public void A_well_formed_amount_is_accepted(string amount) =>
        Parse($$"""{"give": "USD_CASH", "receive": "EUR_WIRE", "amount_give": "{{amount}}"}""").AmountGive.ShouldBe(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture));

    [Theory]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("5")]
    [InlineData("null")]
    public void A_body_that_is_not_an_object_is_refused_as_a_whole(string json) =>
        Refusal(json).ShouldBe(["body"]);

    [Theory]
    [InlineData("""{"receive": "EUR_WIRE", "amount_give": "100"}""", "body.give")]
    [InlineData("""{"give": 1, "receive": "EUR_WIRE", "amount_give": "100"}""", "body.give")]
    [InlineData("""{"give": "usd_cash", "receive": "EUR_WIRE", "amount_give": "100"}""", "body.give")]
    [InlineData("""{"give": "EUR_ATM", "receive": "EUR_WIRE", "amount_give": "100"}""", "body.give")] // a receive code
    [InlineData("""{"give": "USD_CASH", "amount_give": "100"}""", "body.receive")]
    [InlineData("""{"give": "USD_CASH", "receive": "RUB_WIRE", "amount_give": "100"}""", "body.receive")] // a give code
    [InlineData("""{"give": "USD_CASH", "receive": null, "amount_give": "100"}""", "body.receive")]
    public void A_missing_or_unknown_code_is_refused(string json, string field) =>
        Refusal(json).ShouldBe([field]);

    [Theory]
    [InlineData("100")] // a JSON number (D-007)
    [InlineData("100.5")]
    [InlineData("\"0\"")]
    [InlineData("\"0.00\"")]
    [InlineData("\"-1\"")]
    [InlineData("\"+1\"")]
    [InlineData("\"01\"")]
    [InlineData("\"1.\"")]
    [InlineData("\".5\"")]
    [InlineData("\"1.234\"")]
    [InlineData("\"1,5\"")]
    [InlineData("\"1e3\"")]
    [InlineData("\" 1\"")]
    [InlineData("\"\"")]
    [InlineData("\"10000000000000000\"")] // 17 integer digits
    [InlineData("null")]
    [InlineData("true")]
    public void A_malformed_amount_is_refused(string amount) =>
        Refusal($$"""{"give": "USD_CASH", "receive": "EUR_WIRE", "amount_give": {{amount}}}""").ShouldBe(["body.amount_give"]);

    [Fact]
    public void A_missing_amount_is_refused() =>
        Refusal("""{"give": "USD_CASH", "receive": "EUR_WIRE"}""").ShouldBe(["body.amount_give"]);

    [Theory]
    [InlineData("\"1\"")] // a string is not an id
    [InlineData("true")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("1.0")]
    [InlineData("1e0")]
    [InlineData("2147483648")]
    [InlineData("[1]")]
    public void A_malformed_id_is_refused(string id)
    {
        foreach (var field in DetailFields.Priced)
        {
            Refusal($$"""{"give": "USD_CASH", "receive": "EUR_WIRE", "amount_give": "100", "{{field}}": {{id}}}""").ShouldBe(["body." + field]);
        }
    }

    [Fact]
    public void The_largest_id_is_accepted() =>
        Parse("""{"give": "USD_CASH", "receive": "EUR_CASH", "amount_give": "100", "city_id": 2147483647}""").ReferenceIds["city_id"].ShouldBe(int.MaxValue);

    [Fact]
    public void Every_bad_field_is_listed_in_request_order()
    {
        var fields = Refusal("""{"network_receive_id": "1", "network_give_id": 0, "bank_receive_id": 1.5, "bank_give_id": -1, "city_id": true, "amount_give": 5, "receive": "y", "give": "x"}""");

        fields.ShouldBe(["body.give", "body.receive", "body.amount_give", "body.city_id", "body.bank_give_id", "body.bank_receive_id", "body.network_give_id", "body.network_receive_id"]);
    }

    [Fact]
    public void A_refusal_is_invalid_request()
    {
        using var document = JsonDocument.Parse("""{"give": "x"}""");

        Should.Throw<PricingException>(() => PricingRequest.Parse(document.RootElement)).Kind.ShouldBe("invalid_request");
    }

    private static PricingInput Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return PricingRequest.Parse(document.RootElement);
    }

    private static IReadOnlyList<string> Refusal(string json)
    {
        using var document = JsonDocument.Parse(json);
        return Should.Throw<PricingException>(() => PricingRequest.Parse(document.RootElement)).Fields;
    }
}
