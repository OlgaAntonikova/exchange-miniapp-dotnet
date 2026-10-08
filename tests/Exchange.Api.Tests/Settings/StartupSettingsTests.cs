using Exchange.Api.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace Exchange.Api.Tests.Settings;

/// <summary>APP_ENV from the process decides both environments (spec 1.11, D-017); a failed start is
/// covered by <see cref="StartupFailureTests"/>, which runs the real process.</summary>
[Collection(nameof(ProcessEnvironmentGroup))]
public sealed class StartupSettingsTests
{
    [Fact]
    public void App_env_from_the_process_sets_both_environments()
    {
        using var factory = new TestAppFactory(TestSettings.Production());
        using var client = factory.CreateClient();

        factory.Services.GetRequiredService<AppSettings>().Environment.ShouldBe(AppEnvironment.Test);
        factory.Services.GetRequiredService<IHostEnvironment>().EnvironmentName.ShouldBe(Environments.Production);
    }

    [Fact]
    public void App_env_in_the_host_configuration_does_not_switch_the_app()
    {
        var environment = TestSettings.Production();
        environment["APP_ENV"] = "demo";
        using var factory = new TestAppFactory(environment);
        using var client = factory.CreateClient();

        factory.Services.GetRequiredService<AppSettings>().Environment.ShouldBe(AppEnvironment.Test);
    }

    [Fact]
    public void The_host_starts_in_demo_mode_without_any_secret()
    {
        ProcessEnvironment.With("demo", () =>
        {
            using var factory = new TestAppFactory(TestSettings.Demo());
            using var client = factory.CreateClient();

            factory.Services.GetRequiredService<AppSettings>().Environment.ShouldBe(AppEnvironment.Demo);
        });
    }
}
