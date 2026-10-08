using Exchange.Domain.Directions;
using Shouldly;

namespace Exchange.Domain.Tests.Directions;

/// <summary>
/// INV-6 and spec 1.1: the table below is written from the spec, independently of the
/// implementation's, and every row is checked both ways - accepted with its fields, refused without
/// each of them and with each field it does not use.
/// </summary>
public sealed class DirectionTests
{
    private const string City = "city_id";
    private const string BankGive = "bank_give_id";
    private const string BankReceive = "bank_receive_id";
    private const string NetworkGive = "network_give_id";
    private const string NetworkReceive = "network_receive_id";
    private const string TransferTime = "usdt_transfer_time";
    private const string Iban = "iban";
    private const string Recipient = "recipient_name";
    private const string Wallet = "wallet_address";
    private const string PaymentPurpose = "payment_purpose";

    // Spec 1.1, side by side.
    private static readonly Dictionary<string, string[]> FromRequired = new()
    {
        ["RUB_WIRE"] = [BankGive],
        ["USDT"] = [NetworkGive, TransferTime],
        ["USD_CASH"] = [],
        ["EUR_CASH"] = [],
    };

    private static readonly Dictionary<string, string[]> ToRequired = new()
    {
        ["EUR_CASH"] = [City],
        ["EUR_ATM"] = [BankReceive, Iban, Recipient],
        ["EUR_WIRE"] = [Iban, Recipient],
        ["EUR_INVOICE"] = [Iban, Recipient],
        ["USDT"] = [NetworkReceive, Wallet],
    };

    private static readonly Dictionary<string, string[]> ToOptional = new() { ["EUR_WIRE"] = [PaymentPurpose], ["EUR_INVOICE"] = [PaymentPurpose] };

    private static readonly string[] PricedFields = [City, BankGive, BankReceive, NetworkGive, NetworkReceive];
    private static readonly string[] OrderFields = [.. PricedFields, TransferTime, Iban, Recipient, Wallet, PaymentPurpose];

    // Spec 1.1: same base currency is refused (EUR to EUR, USD or USDT to USDT).
    private static readonly (string Give, string Receive)[] SameBasePairs =
    [
        ("EUR_CASH", "EUR_CASH"), ("EUR_CASH", "EUR_ATM"), ("EUR_CASH", "EUR_WIRE"), ("EUR_CASH", "EUR_INVOICE"),
        ("USD_CASH", "USDT"), ("USDT", "USDT"),
    ];

    private static readonly (string Give, string Receive)[] ValidPairList =
        [.. FromRequired.Keys.SelectMany(give => ToRequired.Keys.Select(receive => (give, receive))).Where(pair => !SameBasePairs.Contains(pair))];

    public static TheoryData<string, string> ValidPairs() => [.. ValidPairList];

    public static TheoryData<string, string> InvalidPairs() => [.. SameBasePairs];

    // Spec 1.1: four gives times five receives, less the same-base pairs.
    private static readonly int ValidPairCount = (FromRequired.Count * ToRequired.Count) - SameBasePairs.Length;

    [Fact]
    public void The_table_holds_exactly_the_fourteen_valid_pairs_of_the_spec()
    {
        var actual = Exchange.Domain.Directions.Directions.All.Select(d => (Codes.Of(d.Give), Codes.Of(d.Receive))).ToList();

        actual.Count.ShouldBe(ValidPairCount);
        actual.ShouldBe(ValidPairList, ignoreOrder: true);
    }

    [Theory]
    [MemberData(nameof(ValidPairs))]
    public void A_direction_requires_and_allows_what_the_spec_lists(string give, string receive)
    {
        var (g, r) = Parse(give, receive);
        var orderRequired = FromRequired[give].Concat(ToRequired[receive]).ToHashSet();
        var optional = ToOptional.GetValueOrDefault(receive, []).ToHashSet();

        var order = Exchange.Domain.Directions.Directions.FieldsFor(g, r, Purpose.Order);
        var quote = Exchange.Domain.Directions.Directions.FieldsFor(g, r, Purpose.Quote);

        order.Required.ShouldBe(orderRequired, ignoreOrder: true);
        order.Optional.ShouldBe(optional, ignoreOrder: true);
        quote.Required.ShouldBe(orderRequired.Intersect(PricedFields), ignoreOrder: true);
        quote.Optional.ShouldBeEmpty(); // payment_purpose is never priced
    }

    [Theory]
    [MemberData(nameof(ValidPairs))]
    public void A_direction_with_its_fields_is_accepted_for_both_purposes(string give, string receive)
    {
        var (g, r) = Parse(give, receive);
        var orderFields = FromRequired[give].Concat(ToRequired[receive]).Concat(ToOptional.GetValueOrDefault(receive, []));

        Check(g, r, orderFields, Purpose.Order).ShouldBeNull();
        Check(g, r, orderFields.Intersect(PricedFields), Purpose.Quote).ShouldBeNull();
    }

    [Theory]
    [MemberData(nameof(ValidPairs))]
    public void Each_missing_required_field_is_refused_by_name(string give, string receive)
    {
        var (g, r) = Parse(give, receive);
        var required = FromRequired[give].Concat(ToRequired[receive]).ToList();

        foreach (var field in required)
        {
            var problem = Check(g, r, required.Where(name => name != field), Purpose.Order).ShouldNotBeNull();
            problem.SameBase.ShouldBeFalse();
            problem.Missing.ShouldBe([field]);
            problem.Unexpected.ShouldBeEmpty();
        }

        // A quote needs its priced fields too: without one it would be priced with no markup.
        var quoteRequired = required.Intersect(PricedFields).ToList();
        foreach (var field in quoteRequired)
        {
            var problem = Check(g, r, quoteRequired.Where(name => name != field), Purpose.Quote).ShouldNotBeNull();
            problem.SameBase.ShouldBeFalse();
            problem.Missing.ShouldBe([field]);
            problem.Unexpected.ShouldBeEmpty();
        }
    }

    [Theory]
    [MemberData(nameof(ValidPairs))]
    public void Each_accepted_field_the_direction_does_not_use_is_refused_by_name(string give, string receive)
    {
        var (g, r) = Parse(give, receive);
        var used = FromRequired[give].Concat(ToRequired[receive]).Concat(ToOptional.GetValueOrDefault(receive, [])).ToList();
        var required = FromRequired[give].Concat(ToRequired[receive]).ToList();

        foreach (var field in OrderFields.Except(used))
        {
            Check(g, r, required.Append(field), Purpose.Order).ShouldNotBeNull().Unexpected.ShouldBe([field]);
        }

        var quoteRequired = required.Intersect(PricedFields).ToList();
        foreach (var field in PricedFields.Except(used))
        {
            Check(g, r, quoteRequired.Append(field), Purpose.Quote).ShouldNotBeNull().Unexpected.ShouldBe([field]);
        }
    }

    [Fact]
    public void A_quote_ignores_order_only_fields_and_unknown_names()
    {
        var problem = Check(GiveCode.RubWire, ReceiveCode.EurAtm, [BankGive, BankReceive, Iban, Recipient, "amount_receive", "rate"], Purpose.Quote);

        problem.ShouldBeNull();
    }

    [Fact]
    public void Several_problems_are_reported_together_and_sorted()
    {
        // Given in reverse order, so the sort is observable.
        var problem = Check(GiveCode.Usdt, ReceiveCode.EurAtm, [Wallet, City], Purpose.Order).ShouldNotBeNull();

        problem.Missing.ShouldBe([BankReceive, Iban, NetworkGive, Recipient, TransferTime]);
        problem.Unexpected.ShouldBe([City, Wallet]);
    }

    [Theory]
    [MemberData(nameof(InvalidPairs))]
    public void A_same_base_direction_is_refused_whatever_its_fields(string give, string receive)
    {
        var (g, r) = Parse(give, receive);

        Check(g, r, OrderFields, Purpose.Order).ShouldNotBeNull().SameBase.ShouldBeTrue();
        Check(g, r, [], Purpose.Quote).ShouldNotBeNull().SameBase.ShouldBeTrue();
        Exchange.Domain.Directions.Directions.All.ShouldNotContain(new Direction(g, r));
    }

    [Theory]
    [InlineData("RUB_WIRE", Currency.Rub)]
    [InlineData("USD_CASH", Currency.Usd)]
    [InlineData("EUR_CASH", Currency.Eur)]
    [InlineData("USDT", Currency.Usd)]
    public void Give_codes_map_to_their_base_currency(string give, Currency expected)
    {
        Codes.TryParseGive(give, out var code).ShouldBeTrue();
        Exchange.Domain.Directions.Directions.BaseOf(code).ShouldBe(expected);
    }

    [Theory]
    [InlineData("EUR_CASH", Currency.Eur)]
    [InlineData("EUR_ATM", Currency.Eur)]
    [InlineData("EUR_WIRE", Currency.Eur)]
    [InlineData("EUR_INVOICE", Currency.Eur)]
    [InlineData("USDT", Currency.Usd)]
    public void Receive_codes_map_to_their_base_currency(string receive, Currency expected)
    {
        Codes.TryParseReceive(receive, out var code).ShouldBeTrue();
        Exchange.Domain.Directions.Directions.BaseOf(code).ShouldBe(expected);
    }

    private static (GiveCode Give, ReceiveCode Receive) Parse(string give, string receive)
    {
        Codes.TryParseGive(give, out var g).ShouldBeTrue(give);
        Codes.TryParseReceive(receive, out var r).ShouldBeTrue(receive);
        return (g, r);
    }

    private static DirectionProblem? Check(GiveCode give, ReceiveCode receive, IEnumerable<string> provided, Purpose purpose) =>
        Exchange.Domain.Directions.Directions.Check(give, receive, provided, purpose);
}
