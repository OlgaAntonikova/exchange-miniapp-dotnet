using System.Diagnostics;
using System.Net;
using Exchange.Api.Tests.Repository;
using Exchange.Api.Tests.Settings;
using Shouldly;

namespace Exchange.Api.Tests.Web;

/// <summary>
/// D-043: the real app, started from a directory that holds none of its files, serves the page it
/// was built with. It listens on 127.0.0.2, so a developer's own server on 127.0.0.1:8000 is left
/// alone; serving the page needs no database.
/// </summary>
public sealed class WebRootTests
{
    private const string ServeHost = "127.0.0.2";
    private const int ServePort = 8000;
    private const int StartTimeoutSeconds = 60;

    [Fact]
    public async Task The_real_app_serves_its_page_whatever_the_working_directory()
    {
        var appPath = Path.Combine(AppContext.BaseDirectory, "Exchange.Api.dll");
        var workingDirectory = Directory.CreateTempSubdirectory("exchange-cwd-").FullName;
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(appPath);
        foreach (var (name, value) in TestSettings.Production())
        {
            start.Environment[name] = value;
        }

        start.Environment["APP_ENV"] = "production";
        start.Environment["SERVE_HOST"] = ServeHost;
        start.Environment["DATABASE_URL"] = "postgresql://postgres:unused@127.0.0.1:1/unused";

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var errors = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        string? page;
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://{ServeHost}:{ServePort}") };
            page = await GetPageAsync(client, process);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        }

        // The output is complete only once the process is gone.
        var log = await output + await errors;
        TryDelete(workingDirectory);
        page.ShouldBe(await File.ReadAllTextAsync(Path.Combine(RepositoryRoot.Path, "src", "Exchange.Api", "wwwroot", "index.html"), TestContext.Current.CancellationToken), log);
    }

    // The directory can stay locked for a moment after the process tree is gone; it is a temporary
    // directory, so a failed delete is left to the system.
    private static void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static async Task<string?> GetPageAsync(HttpClient client, Process process)
    {
        var deadline = DateTime.UtcNow.AddSeconds(StartTimeoutSeconds);
        while (DateTime.UtcNow < deadline && !process.HasExited)
        {
            try
            {
                using var response = await client.GetAsync(new Uri("/", UriKind.Relative), TestContext.Current.CancellationToken);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
                }
            }
            catch (HttpRequestException)
            {
                // not listening yet
            }

            await Task.Delay(200, TestContext.Current.CancellationToken);
        }

        return null;
    }
}
