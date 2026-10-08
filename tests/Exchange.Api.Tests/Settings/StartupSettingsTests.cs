using Exchange.Api.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace Exchange.Api.Tests.Settings;

/// <summary>INV-8 at the host: a bad setting stops the app before it serves anything; APP_ENV from
/// the process decides both environments (spec 1.11, D-017).</summary>
[Collection(nameof(ProcessEnvironmentGroup))]
public sealed class StartupSettingsTests
{
    [Fact]
    public void The_host_does_not_start_with_a_missing_secret()
    {
        var environment = TestSettings.Production();
        environment.Remove("JWT_SECRET");
        using var factory = new TestAppFactory(environment);

        var error = Should.Throw<Exception>(() => factory.CreateClient());

        var refusal = Unwrap(error).OfType<SettingsException>().ShouldHaveSingleItem();
        refusal.Problems.ShouldBe(["JWT_SECRET is missing"]);
    }

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

    [Fact]
    public void An_unknown_app_env_stops_the_start()
    {
        ProcessEnvironment.With("test,production", () =>
        {
            using var factory = new TestAppFactory(TestSettings.Production());

            var error = Should.Throw<Exception>(() => factory.CreateClient());

            Unwrap(error).OfType<SettingsException>().ShouldHaveSingleItem().Problems
                .ShouldBe(["APP_ENV must be development, test, production or demo"]);
        });
    }

    private static IEnumerable<Exception> Unwrap(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            yield return current;
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions.SelectMany(Unwrap))
                {
                    yield return inner;
                }
            }
        }
    }
}
