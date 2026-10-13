using Exchange.Api.Tests.Data;
using Shouldly;

namespace Exchange.Api.Tests.Repository;

/// <summary>
/// The demo packaging of spec 1.9 and PHASE-7, read from the files: the images are pinned by exact
/// tag (RULE-DEPENDENCIES), the app runs as a non-root user, the database keeps nothing and
/// publishes nothing, the app is reachable from the host's loopback only, and nothing secret goes
/// into the image. Running the demo is checked by hand at the PHASE-7 gate.
/// </summary>
public sealed class PackagingTests
{
    private static readonly string Root = RepositoryRoot.Path;

    [Fact]
    public void The_image_builds_with_the_pinned_sdk_and_runs_on_the_pinned_runtime()
    {
        var froms = Lines("Dockerfile").Where(line => line.StartsWith("FROM ", StringComparison.Ordinal)).ToList();

        froms.ShouldBe(["FROM mcr.microsoft.com/dotnet/sdk:10.0.400 AS build", "FROM mcr.microsoft.com/dotnet/aspnet:10.0.11 AS runtime"]);
        File.ReadAllText(Path.Combine(Root, "global.json")).ShouldContain("\"version\": \"10.0.400\"");
    }

    [Fact]
    public void The_app_runs_as_the_images_non_root_user()
    {
        var lines = Lines("Dockerfile");
        var runtime = lines.FindIndex(line => line.StartsWith("FROM mcr.microsoft.com/dotnet/aspnet:", StringComparison.Ordinal));

        lines.Skip(runtime).ShouldContain("USER $APP_UID");
        lines.Skip(runtime).Last(line => line.StartsWith("USER ", StringComparison.Ordinal)).ShouldBe("USER $APP_UID");
    }

    [Fact]
    public void The_runtime_images_default_port_setting_is_cleared()
    {
        // The app listens on its own fixed port; the image's ASPNETCORE_HTTP_PORTS would only make
        // Kestrel warn at every start (D-044).
        var lines = Lines("Dockerfile");
        var runtime = lines.FindIndex(line => line.StartsWith("FROM mcr.microsoft.com/dotnet/aspnet:", StringComparison.Ordinal));

        lines.Skip(runtime).ShouldContain("ENV ASPNETCORE_HTTP_PORTS=");
    }

    [Fact]
    public void The_compose_database_is_the_tested_image_keeps_nothing_and_publishes_no_port()
    {
        var db = Service("db");

        db.ShouldContain($"    image: {PostgresFixture.Image}");
        db.ShouldContain("      POSTGRES_HOST_AUTH_METHOD: trust");
        db.ShouldContain("    tmpfs:");
        db.ShouldContain("      - /var/lib/postgresql/data");
        db.ShouldNotContain(line => line.TrimStart().StartsWith("ports:", StringComparison.Ordinal));
        db.ShouldNotContain(line => line.TrimStart().StartsWith("volumes:", StringComparison.Ordinal));
    }

    [Fact]
    public void The_compose_app_runs_the_demo_on_the_hosts_loopback_only()
    {
        var app = Service("app");

        app.ShouldContain("      APP_ENV: demo");
        app.ShouldContain("      DATABASE_URL: postgresql://postgres@db:5432/exchange"); // no password (INV-16)
        var published = app.SkipWhile(line => line != "    ports:").Skip(1).TakeWhile(line => line.StartsWith("      - ", StringComparison.Ordinal)).ToList();
        published.ShouldBe(["      - \"127.0.0.1:8000:8000\""]);
    }

    [Fact]
    public void The_compose_file_sets_no_production_secret()
    {
        var text = File.ReadAllText(Path.Combine(Root, "docker-compose.yml"));

        foreach (var name in new[] { "BOT_TOKEN", "BOT_WEBHOOK_SECRET", "JWT_SECRET", "PII_ENCRYPTION_KEY", "PGPASSWORD", "POSTGRES_PASSWORD" })
        {
            text.ShouldNotContain(name);
        }
    }

    [Theory]
    [InlineData(".env")]
    [InlineData(".env.*")]
    [InlineData("**/.env")]
    [InlineData(".git")]
    [InlineData("**/Properties/launchSettings.json")]
    [InlineData("tests")]
    public void Nothing_secret_or_local_goes_into_the_image(string pattern) =>
        Lines(".dockerignore").ShouldContain(pattern);

    private static List<string> Lines(string file) =>
        [.. File.ReadAllLines(Path.Combine(Root, file)).Select(line => line.TrimEnd())];

    // The lines of one service of docker-compose.yml: from "  <name>:" to the next service or the end.
    private static List<string> Service(string name)
    {
        var lines = Lines("docker-compose.yml");
        var start = lines.IndexOf($"  {name}:");
        start.ShouldBeGreaterThanOrEqualTo(0, $"no service {name}");
        return [.. lines.Skip(start + 1).TakeWhile(line => line.Length == 0 || line.StartsWith("    ", StringComparison.Ordinal))];
    }
}
