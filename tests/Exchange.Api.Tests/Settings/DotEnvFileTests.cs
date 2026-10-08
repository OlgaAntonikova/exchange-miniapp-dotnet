using Exchange.Api.Settings;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace Exchange.Api.Tests.Settings;

/// <summary>D-017: the <c>.env</c> reader.</summary>
public sealed class DotEnvFileTests
{
    [Fact]
    public void Pairs_comments_blanks_and_quotes_are_read()
    {
        var pairs = DotEnvFile.Parse("# comment\r\n\nA=1\nB = two words \nC=\"quoted value\"\nD='single'\nE=\nF=x=y\nA=3\n");

        pairs.ShouldBe(new Dictionary<string, string>
        {
            ["A"] = "3",
            ["B"] = "two words",
            ["C"] = "quoted value",
            ["D"] = "single",
            ["E"] = string.Empty,
            ["F"] = "x=y",
        });
    }

    [Fact]
    public void The_process_environment_wins_over_env_and_env_fills_the_rest()
    {
        var builder = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["LOG_FORMAT"] = "json" });

        DotEnvFile.AddBelow(builder, "LOG_FORMAT=console\nLOG_LEVEL=Debug\n");
        var configuration = builder.Build();

        configuration["LOG_FORMAT"].ShouldBe("json");
        configuration["LOG_LEVEL"].ShouldBe("Debug");
    }

    [Theory]
    [InlineData("APP_ENV=demo")]
    [InlineData("APP_ENV=development")]
    public void App_env_in_env_is_refused(string line)
    {
        var error = Should.Throw<SettingsException>(() => DotEnvFile.AddBelow(new ConfigurationBuilder(), line));

        error.Problems.ShouldBe(["APP_ENV is read from the process environment only; remove it from .env"]);
    }

    [Theory]
    [InlineData("JUST_A_KEY")]
    [InlineData("=value")]
    [InlineData("TWO WORDS=value")]
    public void A_malformed_line_is_refused_by_number_without_its_text(string line)
    {
        var error = Should.Throw<FormatException>(() => DotEnvFile.Parse("OK=1\n" + line));

        error.Message.ShouldBe(".env line 2 is not KEY=VALUE");
    }
}
