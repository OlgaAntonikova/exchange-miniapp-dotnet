namespace Exchange.Api.Settings;

/// <summary>
/// The settings cannot start the app. <see cref="Problems"/> name each variable and what is wrong
/// with it, never its value (INV-8).
/// </summary>
public sealed class SettingsException : Exception
{
    /// <summary>Creates the exception from every problem found.</summary>
    public SettingsException(IReadOnlyList<string> problems)
        : base("Invalid settings: " + string.Join("; ", problems)) => Problems = problems;

    /// <summary>Creates an exception with one problem.</summary>
    public SettingsException(string message)
        : this([message])
    {
    }

    /// <summary>Required by the exception pattern; prefer the list constructor.</summary>
    public SettingsException()
        : this("unknown problem")
    {
    }

    /// <summary>Required by the exception pattern; prefer the list constructor.</summary>
    public SettingsException(string message, Exception innerException)
        : base(message, innerException) => Problems = [message];

    /// <summary>One line per problem, each naming its variable.</summary>
    public IReadOnlyList<string> Problems { get; }
}
