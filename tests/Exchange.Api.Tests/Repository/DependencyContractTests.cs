using Shouldly;

namespace Exchange.Api.Tests.Repository;

/// <summary>INV-20: versions live in one file, Microsoft packages track the runtime, forbidden
/// packages stay out, and only the logging setup writes to the console.</summary>
public sealed class DependencyContractTests
{
    private const int ProjectCount = 4;

    private static readonly string Root = RepositoryRoot.Path;

    [Fact]
    public void No_project_gives_a_package_its_own_version()
    {
        foreach (var project in ProjectFiles())
        {
            DependencyContract.ReferencesWithOwnVersion(File.ReadAllText(project)).ShouldBeEmpty(project);
        }
    }

    [Fact]
    public void Every_referenced_package_is_declared_centrally()
    {
        var declared = DependencyContract.DeclaredPackages(File.ReadAllText(Path.Combine(Root, "Directory.Packages.props")));
        var referenced = ProjectFiles().SelectMany(project => DependencyContract.PackageReferences(File.ReadAllText(project))).ToList();

        referenced.ShouldNotBeEmpty();
        referenced.Except(declared, StringComparer.OrdinalIgnoreCase).ShouldBeEmpty();
    }

    [Fact]
    public void Central_versions_cannot_be_overridden()
    {
        var props = System.Xml.Linq.XDocument.Load(Path.Combine(Root, "Directory.Packages.props"));

        props.Descendants("CentralPackageVersionOverrideEnabled").Select(element => element.Value.Trim())
            .ShouldBe(["false"]);
    }

    [Fact]
    public void Restored_graphs_hold_no_forbidden_package_and_the_pinned_framework()
    {
        var assets = AssetsFiles();
        assets.Count.ShouldBe(ProjectCount, "every project must be restored before this test can judge it");

        foreach (var file in assets)
        {
            var json = File.ReadAllText(file);
            DependencyContract.ForbiddenPackages(json).ShouldBeEmpty(file);
            DependencyContract.UnpinnedFrameworkPackages(json).ShouldBeEmpty(file);
        }
    }

    [Fact]
    public void Only_the_logging_setup_writes_to_the_console()
    {
        var sources = Directory.GetFiles(Path.Combine(Root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !IsBuildOutput(file))
            .ToList();
        sources.ShouldNotBeEmpty();

        foreach (var file in sources)
        {
            DependencyContract.ConsoleUses(Path.GetRelativePath(Root, file), File.ReadAllText(file)).ShouldBeEmpty(file);
        }
    }

    // --- The checks reject what they are for (RULE-NEGATIVE-TEST) ---------------------------------

    [Theory]
    [InlineData("""<Project><ItemGroup><PackageReference Include="Serilog" Version="4.3.0" /></ItemGroup></Project>""")]
    [InlineData("""<Project><ItemGroup><PackageReference Include="Serilog" VersionOverride="4.3.0" /></ItemGroup></Project>""")]
    [InlineData("""<Project><ItemGroup><PackageReference Include="Serilog"><Version>4.3.0</Version></PackageReference></ItemGroup></Project>""")]
    public void A_reference_with_its_own_version_is_reported(string projectXml)
    {
        DependencyContract.ReferencesWithOwnVersion(projectXml).ShouldBe(["Serilog"]);
    }

    [Theory]
    [InlineData("Moq")]
    [InlineData("FluentAssertions")]
    [InlineData("Polly")]
    [InlineData("Polly.Core")]
    [InlineData("Polly.Extensions")]
    [InlineData("Microsoft.Extensions.Resilience")]
    [InlineData("Swashbuckle.AspNetCore")]
    [InlineData("NSwag.AspNetCore")]
    [InlineData("Scalar.AspNetCore")]
    [InlineData("Microsoft.NET.Test.Sdk")]
    [InlineData("xunit.runner.visualstudio")]
    public void A_forbidden_package_in_the_graph_is_reported(string package)
    {
        var assets = Assets(libraries: [$"{package}/1.0.0"], dependencies: []);

        DependencyContract.ForbiddenPackages(assets).ShouldBe([package]);
    }

    // The real shape of EF Design 10.0.11: it depends on Newtonsoft.Json directly and through
    // Microsoft.CodeAnalysis.Workspaces.MSBuild.
    private static readonly string[] EfDesignGraph =
        ["Microsoft.EntityFrameworkCore.Design/10.0.11", "Microsoft.CodeAnalysis.Workspaces.MSBuild/5.0.0", "Newtonsoft.Json/13.0.4"];

    private static readonly (string, string)[] EfDesignEdges =
    [
        ("Microsoft.EntityFrameworkCore.Design/10.0.11", "Newtonsoft.Json"),
        ("Microsoft.EntityFrameworkCore.Design/10.0.11", "Microsoft.CodeAnalysis.Workspaces.MSBuild"),
        ("Microsoft.CodeAnalysis.Workspaces.MSBuild/5.0.0", "Newtonsoft.Json"),
    ];

    [Fact]
    public void Newtonsoft_reached_only_through_the_ef_design_package_is_allowed()
    {
        var assets = Assets(libraries: EfDesignGraph, dependencies: EfDesignEdges, direct: ["Microsoft.EntityFrameworkCore.Design >= 10.0.11"]);

        DependencyContract.ForbiddenPackages(assets).ShouldBeEmpty();
    }

    [Fact]
    public void Newtonsoft_reached_through_an_ef_design_package_that_arrives_transitively_is_allowed()
    {
        // A project reference brings the EF design package; Newtonsoft is still reached only
        // through it.
        var assets = Assets(
            libraries: [.. EfDesignGraph, "Exchange.Api/1.0.0"],
            dependencies: [.. EfDesignEdges, ("Exchange.Api/1.0.0", "Microsoft.EntityFrameworkCore.Design")],
            direct: ["Exchange.Api >= 1.0.0"]);

        DependencyContract.ForbiddenPackages(assets).ShouldBeEmpty();
    }

    [Fact]
    public void Newtonsoft_also_reachable_without_the_ef_design_package_is_reported()
    {
        // Some.Library reaches Newtonsoft through a package EF Design also uses: still reachable
        // once EF Design is taken out.
        var assets = Assets(
            libraries: [.. EfDesignGraph, "Some.Library/1.0.0"],
            dependencies: [.. EfDesignEdges, ("Some.Library/1.0.0", "Microsoft.CodeAnalysis.Workspaces.MSBuild")],
            direct: ["Microsoft.EntityFrameworkCore.Design >= 10.0.11", "Some.Library >= 1.0.0"]);

        DependencyContract.ForbiddenPackages(assets).ShouldBe(["Newtonsoft.Json"]);
    }

    [Fact]
    public void Newtonsoft_referenced_by_the_project_itself_is_reported()
    {
        var assets = Assets(
            libraries: EfDesignGraph,
            dependencies: EfDesignEdges,
            direct: ["Microsoft.EntityFrameworkCore.Design >= 10.0.11", "Newtonsoft.Json >= 13.0.4"]);

        DependencyContract.ForbiddenPackages(assets).ShouldBe(["Newtonsoft.Json"]);
    }

    [Fact]
    public void Newtonsoft_with_no_visible_source_is_reported()
    {
        // Nothing shows it arrived through the EF design package, so it is not allowed: fail closed.
        var assets = Assets(libraries: ["Newtonsoft.Json/13.0.4"], dependencies: []);

        DependencyContract.ForbiddenPackages(assets).ShouldBe(["Newtonsoft.Json"]);
    }

    [Theory]
    [InlineData("Microsoft.AspNetCore.Mvc.Testing/10.0.12")]
    [InlineData("Microsoft.EntityFrameworkCore/10.0.4")]
    [InlineData("Microsoft.EntityFrameworkCore.Relational/10.0.10")]
    public void A_framework_package_off_the_pin_is_reported(string library)
    {
        DependencyContract.UnpinnedFrameworkPackages(Assets(libraries: [library], dependencies: [])).Count.ShouldBe(1);
    }

    [Fact]
    public void A_framework_package_on_the_pin_and_other_packages_pass()
    {
        var assets = Assets(libraries: ["Microsoft.AspNetCore.Mvc.Testing/10.0.11", "Microsoft.Extensions.Logging/10.0.0"], dependencies: []);

        DependencyContract.UnpinnedFrameworkPackages(assets).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Console.WriteLine(\"x\");")]
    [InlineData("System.Console.Out.Write(x);")]
    [InlineData("Console .Error.WriteLine(x);")]
    [InlineData("using static System.Console;")]
    [InlineData("using Terminal = System.Console;")]
    public void Console_output_outside_the_logging_setup_is_reported(string line)
    {
        DependencyContract.ConsoleUses("src/Exchange.Api/Orders/Thing.cs", line).Count.ShouldBe(1);
        DependencyContract.ConsoleUses("src/Exchange.Api/Other/LoggingSetup.cs", line).Count.ShouldBe(1);
        DependencyContract.ConsoleUses("src/Exchange.Api/Logging/LoggingSetup.cs", line).ShouldBeEmpty();
        DependencyContract.ConsoleUses("src\\Exchange.Api\\Logging\\LoggingSetup.cs", line).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("builder.Logging.AddConsole();")]
    [InlineData("var consoleWidth = 80;")]
    public void Names_that_merely_contain_console_are_not_reported(string line)
    {
        DependencyContract.ConsoleUses("src/Exchange.Api/Orders/Thing.cs", line).ShouldBeEmpty();
    }

    private static List<string> ProjectFiles() =>
        [.. Directory.GetFiles(Root, "*.csproj", SearchOption.AllDirectories).Where(file => !IsBuildOutput(file))];

    private static List<string> AssetsFiles() =>
        [.. ProjectFiles().Select(project => Path.Combine(Path.GetDirectoryName(project)!, "obj", "project.assets.json")).Where(File.Exists)];

    private static bool IsBuildOutput(string path) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is "bin" or "obj");

    private static string Assets(string[] libraries, (string Library, string Dependency)[] dependencies, string[]? direct = null)
    {
        var libraryEntries = string.Join(",", libraries.Select(name => $"\"{name}\": {{ \"type\": \"package\" }}"));
        var targetEntries = string.Join(",", libraries.Select(name =>
        {
            var deps = dependencies.Where(d => d.Library == name).Select(d => $"\"{d.Dependency}\": \"1.0.0\"");
            return $"\"{name}\": {{ \"dependencies\": {{ {string.Join(",", deps)} }} }}";
        }));
        var directEntries = string.Join(",", (direct ?? []).Select(entry => $"\"{entry}\""));
        return $$"""
            {
              "targets": { "net10.0": { {{targetEntries}} } },
              "libraries": { {{libraryEntries}} },
              "projectFileDependencyGroups": { "net10.0": [ {{directEntries}} ] }
            }
            """;
    }
}
