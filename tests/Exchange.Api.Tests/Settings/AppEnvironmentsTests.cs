using Exchange.Api.Settings;
using Shouldly;

namespace Exchange.Api.Tests.Settings;

/// <summary>Spec 1.11, D-017: APP_ENV takes exactly four values and decides the host's environment.</summary>
public sealed class AppEnvironmentsTests
{
    [Theory]
    [InlineData(null, AppEnvironment.Development)]
    [InlineData("", AppEnvironment.Development)]
    [InlineData("development", AppEnvironment.Development)]
    [InlineData("test", AppEnvironment.Test)]
    [InlineData("production", AppEnvironment.Production)]
    [InlineData(" demo ", AppEnvironment.Demo)]
    public void The_four_names_parse(string? value, AppEnvironment expected) =>
        AppEnvironments.Parse(value).ShouldBe(expected);

    [Theory]
    [InlineData("staging")]
    [InlineData("2")]
    [InlineData("test,production")]
    [InlineData("development, test")]
    [InlineData("Production")]
    [InlineData("demo2")]
    public void Anything_else_is_refused(string value) =>
        Should.Throw<SettingsException>(() => AppEnvironments.Parse(value)).Problems
            .ShouldBe(["APP_ENV must be development, test, production or demo"]);

    [Theory]
    [InlineData(AppEnvironment.Development, "Development")]
    [InlineData(AppEnvironment.Test, "Production")]
    [InlineData(AppEnvironment.Production, "Production")]
    [InlineData(AppEnvironment.Demo, "Production")]
    public void The_host_environment_follows_app_env(AppEnvironment environment, string expected) =>
        AppEnvironments.HostEnvironmentName(environment).ShouldBe(expected);
}
