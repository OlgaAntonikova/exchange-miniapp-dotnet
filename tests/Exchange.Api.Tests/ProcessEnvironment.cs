using System.Runtime.CompilerServices;
using Exchange.Api.Settings;

namespace Exchange.Api.Tests;

/// <summary>
/// The app reads APP_ENV from the process only (D-017), so the test process sets it: <c>test</c>
/// for every host, switched to another value only inside <see cref="ProcessEnvironmentGroup"/>,
/// which xUnit runs alone.
/// </summary>
internal static class ProcessEnvironment
{
    public const string TestValue = "test";

    [ModuleInitializer]
    internal static void RunAsTest() => Environment.SetEnvironmentVariable(AppEnvironments.VariableName, TestValue);

    /// <summary>Runs <paramref name="action"/> with APP_ENV set to <paramref name="value"/>, then restores <c>test</c>.</summary>
    public static void With(string value, Action action)
    {
        Environment.SetEnvironmentVariable(AppEnvironments.VariableName, value);
        try
        {
            action();
        }
        finally
        {
            RunAsTest();
        }
    }
}

/// <summary>Tests that change the process environment; never run beside another test.</summary>
[CollectionDefinition(nameof(ProcessEnvironmentGroup), DisableParallelization = true)]
public sealed class ProcessEnvironmentGroup;
