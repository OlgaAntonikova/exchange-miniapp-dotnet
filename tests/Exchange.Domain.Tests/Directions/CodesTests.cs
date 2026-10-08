using Exchange.Domain.Directions;
using Shouldly;

namespace Exchange.Domain.Tests.Directions;

/// <summary>Spec 1.1 and 1.10: wire codes round-trip, and nothing but the exact code parses.</summary>
public sealed class CodesTests
{
    [Fact]
    public void Every_code_round_trips()
    {
        foreach (var give in Enum.GetValues<GiveCode>())
        {
            Codes.TryParseGive(Codes.Of(give), out var parsed).ShouldBeTrue();
            parsed.ShouldBe(give);
        }

        foreach (var receive in Enum.GetValues<ReceiveCode>())
        {
            Codes.TryParseReceive(Codes.Of(receive), out var parsed).ShouldBeTrue();
            parsed.ShouldBe(receive);
        }

        foreach (var time in Enum.GetValues<UsdtTransferTime>())
        {
            Codes.TryParseTransferTime(Codes.Of(time), out var parsed).ShouldBeTrue();
            parsed.ShouldBe(time);
        }
    }

    [Theory]
    [InlineData("RUB_WIRE", "RUB_WIRE")]
    [InlineData("USD_CASH", "USD_CASH")]
    [InlineData("EUR_CASH", "EUR_CASH")]
    [InlineData("USDT", "USDT")]
    public void Give_codes_are_the_spec_strings(string text, string expected)
    {
        Codes.TryParseGive(text, out var code).ShouldBeTrue();
        Codes.Of(code).ShouldBe(expected);
    }

    [Theory]
    [InlineData("EUR_CASH")]
    [InlineData("EUR_ATM")]
    [InlineData("EUR_WIRE")]
    [InlineData("EUR_INVOICE")]
    [InlineData("USDT")]
    public void Receive_codes_are_the_spec_strings(string text)
    {
        Codes.TryParseReceive(text, out var code).ShouldBeTrue();
        Codes.Of(code).ShouldBe(text);
    }

    [Theory]
    [InlineData("rub_wire")]
    [InlineData(" RUB_WIRE")]
    [InlineData("RUBWIRE")]
    [InlineData("RubWire")]
    [InlineData("0")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("EUR_ATM")] // a receive code is not a give code
    public void Anything_but_an_exact_give_code_is_refused(string? text) => Codes.TryParseGive(text, out _).ShouldBeFalse();

    [Theory]
    [InlineData("eur_atm")]
    [InlineData("EUR-ATM")]
    [InlineData("RUB_WIRE")]
    [InlineData("1")]
    [InlineData(null)]
    public void Anything_but_an_exact_receive_code_is_refused(string? text) => Codes.TryParseReceive(text, out _).ShouldBeFalse();

    [Theory]
    [InlineData("now", true)]
    [InlineData("at_meeting", true)]
    [InlineData("NOW", false)]
    [InlineData("later", false)]
    [InlineData("AtMeeting", false)]
    public void Transfer_times_parse_exactly(string text, bool valid) => Codes.TryParseTransferTime(text, out _).ShouldBe(valid);
}
