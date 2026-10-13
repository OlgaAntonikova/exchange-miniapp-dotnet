using System.Data.Common;
using Exchange.Api.Settings;
using Shouldly;

namespace Exchange.Api.Tests.Settings;

/// <summary>D-010: <c>DATABASE_URL</c> becomes an Npgsql connection string in one place.</summary>
public sealed class DatabaseUrlTests
{
    [Fact]
    public void A_full_url_becomes_a_connection_string()
    {
        var result = DatabaseUrl.Convert("postgresql://exchange:p%40ss%3Aword@db.local:6543/exchange_db?sslmode=require");

        result.Problem.ShouldBeNull();
        result.HasPassword.ShouldBeTrue();
        var parts = Parse(result.ConnectionString!.Reveal());
        parts["host"].ShouldBe("db.local");
        parts["port"].ShouldBe("6543");
        parts["database"].ShouldBe("exchange_db");
        parts["username"].ShouldBe("exchange");
        parts["password"].ShouldBe("p@ss:word");
        parts["ssl mode"].ShouldBe("Require");
        parts["gss encryption mode"].ShouldBe("Disable");
    }

    [Theory]
    [InlineData("postgres://postgres@db/exchange")]
    [InlineData("postgresql://postgres:@db/exchange")]
    [InlineData("postgresql://db/exchange")]
    public void A_url_without_a_password_says_so(string url)
    {
        var result = DatabaseUrl.Convert(url);

        result.Problem.ShouldBeNull();
        result.HasPassword.ShouldBeFalse();
        Parse(result.ConnectionString!.Reveal())["port"].ShouldBe(DatabaseUrl.DefaultPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Parse(result.ConnectionString!.Reveal()).ContainsKey("password").ShouldBeFalse();
    }

    [Fact]
    public void Error_detail_is_never_switched_on()
    {
        var result = DatabaseUrl.Convert("postgresql://u:p@db/exchange");

        result.ConnectionString!.Reveal().ShouldNotContain("Error Detail", Case.Insensitive);
    }

    [Fact]
    public void The_result_prints_without_the_password()
    {
        var result = DatabaseUrl.Convert("postgresql://u:" + "topsecret" + "@db/exchange");

        result.ToString().ShouldNotContain("topsecret");
        $"{result.ConnectionString}".ShouldBe(Secret.Mask);
    }

    [Fact]
    public void An_ipv6_host_reaches_npgsql_without_brackets()
    {
        var result = DatabaseUrl.Convert("postgresql://u@[::1]:5432/exchange");

        Parse(result.ConnectionString!.Reveal())["host"].ShouldBe("::1");
    }

    [Theory]
    [InlineData("mysql://u:p@db/exchange", "is not a postgresql:// URL")]
    [InlineData("postgresql+asyncpg://u:p@db/exchange", "is not a postgresql:// URL")]
    [InlineData("db/exchange", "is not a postgresql:// URL")]
    [InlineData("", "is not a postgresql:// URL")]
    [InlineData("postgresql://u:p@db/", "names no single database")]
    [InlineData("postgresql://u:p@db/a/b", "names no single database")]
    [InlineData("postgresql://u:p@db/exchange#x", "has a fragment")]
    [InlineData("postgresql://u@db/exchange?password=x", "has an unsupported query parameter; only sslmode is allowed")]
    [InlineData("postgresql://u@db/exchange?passfile=/tmp/x", "has an unsupported query parameter; only sslmode is allowed")]
    [InlineData("postgresql://u@db/exchange?sslmode=sometimes", "has an unknown sslmode")]
    [InlineData("postgresql://u@db/exchange?sslmode", "has an unknown sslmode")]
    [InlineData("postgresql://u@db/exchange?sslmode=disable&sslmode=require", "names sslmode twice")]
    public void A_bad_url_is_refused_with_its_problem(string url, string problem)
    {
        var result = DatabaseUrl.Convert(url);

        result.ConnectionString.ShouldBeNull();
        result.Problem.ShouldBe(problem);
    }

    private static Dictionary<string, string> Parse(string connectionString)
    {
        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
        return builder.Keys.Cast<string>().ToDictionary(key => key.ToLowerInvariant(), key => builder[key].ToString()!);
    }
}
