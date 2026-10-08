namespace Exchange.Api.Tests.Repository;

/// <summary>Finds the repository checkout the tests run from.</summary>
internal static class RepositoryRoot
{
    private const string SolutionFileName = "ExchangeMiniApp.slnx";

    /// <summary>The directory holding the solution file, walking up from the test binaries.</summary>
    public static string Path { get; } = Find();

    private static string Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(System.IO.Path.Combine(dir.FullName, SolutionFileName)))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException($"{SolutionFileName} not found above {AppContext.BaseDirectory}");
    }
}
