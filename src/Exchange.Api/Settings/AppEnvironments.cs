namespace Exchange.Api.Settings;

/// <summary>
/// <c>APP_ENV</c>: read once, from the process environment only (D-017), and the single source of
/// both the app's environment and ASP.NET Core's (spec 1.11).
/// </summary>
public static class AppEnvironments
{
    /// <summary>The variable's name.</summary>
    public const string VariableName = "APP_ENV";

    private static readonly Dictionary<string, AppEnvironment> Names = new(StringComparer.Ordinal)
    {
        ["development"] = AppEnvironment.Development,
        ["test"] = AppEnvironment.Test,
        ["production"] = AppEnvironment.Production,
        ["demo"] = AppEnvironment.Demo,
    };

    /// <summary>The environment named by <paramref name="value"/>; unset means development.</summary>
    /// <exception cref="SettingsException">The value is not exactly one of the four names.</exception>
    public static AppEnvironment Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return AppEnvironment.Development;
        }

        return Names.TryGetValue(value.Trim(), out var environment)
            ? environment
            : throw new SettingsException($"{VariableName} must be development, test, production or demo");
    }

    /// <summary>ASP.NET Core's environment name for an app environment.</summary>
    public static string HostEnvironmentName(AppEnvironment environment) =>
        environment == AppEnvironment.Development ? Environments.Development : Environments.Production;
}
