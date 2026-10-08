using Exchange.Api.Tests.Repository;
using Shouldly;

namespace Exchange.Api.Tests;

/// <summary>
/// INV-14: money is exact decimal end to end. This class holds the source scan; the database and
/// API round trips join it with the schema and the endpoints.
/// </summary>
public sealed class MoneyTests
{
    [Fact]
    public void Money_code_holds_no_binary_floating_point_and_no_truncating_rounding()
    {
        var domain = SourceFiles("src/Exchange.Domain");
        var api = SourceFiles("src/Exchange.Api").Where(file => MoneySourceScan.DealsInMoney(File.ReadAllText(file))).ToList();
        domain.ShouldNotBeEmpty();
        api.ShouldNotBeEmpty("the API declares order limits as decimal; the scan must see them");

        foreach (var file in domain.Concat(api))
        {
            MoneySourceScan.Offences(File.ReadAllText(file)).ShouldBeEmpty(file);
        }
    }

    [Theory]
    [InlineData("double factor = 2;", "double")]
    [InlineData("float ratio;", "float")]
    [InlineData("var x = (Double)value;", "Double")]
    [InlineData("var x = (System.Single)value;", "Single")]
    [InlineData("Half h = default;", "Half")]
    [InlineData("var r = MathF.Round(v);", "MathF")]
    [InlineData("var r = Convert.ToDouble(x);", "ToDouble")]
    [InlineData("var r = Convert.ToSingle(x);", "ToSingle")]
    [InlineData("var x = Math.Round(v, 2, MidpointRounding.ToZero);", "ToZero")]
    [InlineData("var x = Math.Round(v, 2, ToZero);", "ToZero")]
    [InlineData("var r = 0.5;", "0.5")]
    [InlineData("var r = (decimal)0.1;", "0.1")]
    [InlineData("var r = 1.5f;", "1.5f")]
    [InlineData("var r = 2d;", "2d")]
    [InlineData("var r = 1e-3;", "1e-3")]
    [InlineData("var r = 2.5E+2;", "2.5E+2")]
    [InlineData("var r = 7F;", "7F")]
    public void The_scan_finds_each_forbidden_form(string code, string expected) =>
        MoneySourceScan.Offences(code).ShouldContain(expected);

    [Theory]
    [InlineData("// double-check the rounding\nvar ok = 1m;")]
    [InlineData("/// <summary>Not MidpointRounding.ToZero, see D-011.</summary>\nvar ok = 1m;")]
    [InlineData("/* a double comment */ var ok = 1m;")]
    [InlineData("var url = \"https://example.org/0.5\"; var ok = 1.5m;")]
    [InlineData("var host = \"127.0.0.1\";")]
    [InlineData("var doubled = Quantize.Amount(x * 2m);")]
    [InlineData("var r = 0.5m + 1M + 1_000m + 2.5000m;")]
    [InlineData("var count = 42; var big = 10_000;")]
    [InlineData("var c = 'd';")]
    public void Prose_strings_and_decimal_literals_are_not_offences(string code) =>
        MoneySourceScan.Offences(code).ShouldBeEmpty();

    [Fact]
    public void Code_after_a_url_in_a_string_is_still_scanned() =>
        MoneySourceScan.Offences("var url = \"https://example.org\"; double d = 1;").ShouldContain("double");

    [Theory]
    [InlineData("public decimal Amount { get; init; }", true)]
    [InlineData("decimal? limit = null;", true)]
    [InlineData("var amount = 100m;", true)]
    [InlineData("decimal[] amounts = [];", true)]
    [InlineData("List<decimal> amounts = [];", true)]
    [InlineData("Func<decimal, decimal> f = x => x;", true)]
    [InlineData("var count = 3;", false)]
    [InlineData("// a decimal comment", false)]
    [InlineData("var s = \"decimal\";", false)]
    public void Money_files_are_recognised(string code, bool dealsInMoney) =>
        MoneySourceScan.DealsInMoney(code).ShouldBe(dealsInMoney);

    private static List<string> SourceFiles(string relative) =>
        [.. Directory.GetFiles(Path.Combine(RepositoryRoot.Path, relative), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is "bin" or "obj"))];
}
