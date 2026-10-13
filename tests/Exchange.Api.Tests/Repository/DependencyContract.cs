using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Exchange.Api.Tests.Repository;

/// <summary>
/// The checks of INV-20, as pure functions over file contents so that each can be shown to reject
/// a broken input (RULE-NEGATIVE-TEST).
/// </summary>
internal static partial class DependencyContract
{
    /// <summary>The runtime every Microsoft ASP.NET Core and EF Core package must match (D-001).</summary>
    public const string PinnedFrameworkVersion = "10.0.11";

    /// <summary>The design-time package allowed to bring Newtonsoft.Json (spec 6.2).</summary>
    public const string EfDesignPackage = "Microsoft.EntityFrameworkCore.Design";

    private const string NewtonsoftPackage = "Newtonsoft.Json";

    /// <summary>The one file allowed to write to the console (spec 4), relative to the root.</summary>
    public const string LoggingSetupPath = "src/Exchange.Api/Logging/LoggingSetup.cs";

    private static readonly string[] ForbiddenExact =
    [
        "FluentAssertions",
        "MediatR",
        "AutoMapper",
        "Moq",
        NewtonsoftPackage,
        "Polly",
        "Microsoft.Extensions.Http.Resilience",
        "Microsoft.Extensions.Resilience",
        "Telegram.Bot",
        "Microsoft.NET.Test.Sdk",
        "xunit.runner.visualstudio",
    ];

    // Polly v8 ships as Polly.Core and Polly.Extensions; OpenAPI UIs as Swashbuckle, NSwag, Scalar.
    private static readonly string[] ForbiddenPrefixes = ["Polly.", "Swashbuckle.", "NSwag.", "Scalar."];

    private static readonly string[] PinnedPrefixes = ["Microsoft.AspNetCore.", "Microsoft.EntityFrameworkCore"];

    /// <summary>Package ids referenced by a project file.</summary>
    public static IReadOnlyList<string> PackageReferences(string projectXml) =>
        [.. XDocument.Parse(projectXml).Descendants("PackageReference")
            .Select(element => (string?)element.Attribute("Include") ?? string.Empty)];

    /// <summary>Package references that carry a version of their own (attribute or element).</summary>
    public static IReadOnlyList<string> ReferencesWithOwnVersion(string projectXml) =>
        [.. XDocument.Parse(projectXml).Descendants("PackageReference")
            .Where(element => element.Attribute("Version") is not null
                || element.Attribute("VersionOverride") is not null
                || element.Element("Version") is not null)
            .Select(element => (string?)element.Attribute("Include") ?? string.Empty)];

    /// <summary>Package ids declared in <c>Directory.Packages.props</c>.</summary>
    public static IReadOnlyList<string> DeclaredPackages(string propsXml) =>
        [.. XDocument.Parse(propsXml).Descendants("PackageVersion")
            .Select(element => (string?)element.Attribute("Include") ?? string.Empty)];

    /// <summary>Forbidden packages present in a restored graph, with the Newtonsoft exception applied.</summary>
    public static IReadOnlyList<string> ForbiddenPackages(string assetsJson)
    {
        using var document = JsonDocument.Parse(assetsJson);
        var libraries = LibraryNames(document);
        var found = libraries.Where(IsForbidden).ToList();

        if (found.Contains(NewtonsoftPackage, StringComparer.OrdinalIgnoreCase) && OnlyThroughEfDesign(document))
        {
            found.RemoveAll(name => name.Equals(NewtonsoftPackage, StringComparison.OrdinalIgnoreCase));
        }

        return found;
    }

    /// <summary>Microsoft ASP.NET Core and EF Core packages resolved at a version other than the pin.</summary>
    public static IReadOnlyList<string> UnpinnedFrameworkPackages(string assetsJson)
    {
        using var document = JsonDocument.Parse(assetsJson);
        return [.. LibraryKeys(document)
            .Select(key => key.Split('/'))
            .Where(parts => PinnedPrefixes.Any(prefix => parts[0].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                && parts[1] != PinnedFrameworkVersion)
            .Select(parts => $"{parts[0]} {parts[1]}")];
    }

    /// <summary>
    /// Lines of a source file that name the console in any form (<c>Console.</c>,
    /// <c>using static System.Console</c>, an alias), unless the file is the logging setup.
    /// </summary>
    /// <param name="relativePath">The file's path relative to the repository root.</param>
    /// <param name="source">The file's text.</param>
    public static IReadOnlyList<string> ConsoleUses(string relativePath, string source) =>
        relativePath.Replace('\\', '/') == LoggingSetupPath
            ? []
            : [.. source.Split('\n').Where(line => ConsolePattern().IsMatch(line)).Select(line => line.Trim())];

    private static bool IsForbidden(string name) =>
        ForbiddenExact.Contains(name, StringComparer.OrdinalIgnoreCase)
        || ForbiddenPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> LibraryKeys(JsonDocument document) =>
        document.RootElement.GetProperty("libraries").EnumerateObject().Select(property => property.Name);

    private static IEnumerable<string> LibraryNames(JsonDocument document) =>
        LibraryKeys(document).Select(key => key.Split('/')[0]);

    // "Reached only through the EF design package" (INV-20): it is reachable from the project's
    // direct dependencies, and it stops being reachable once the EF design package is taken out of
    // the graph - wherever that package sits in it. A direct reference stays reachable, and so does
    // any other path; no visible source at all is reported too: the check fails closed.
    private static bool OnlyThroughEfDesign(JsonDocument document)
    {
        var roots = DirectDependencies(document);
        var graph = DependencyGraph(document);
        return Reachable(roots, graph, skip: null).Contains(NewtonsoftPackage)
            && !Reachable(roots, graph, skip: EfDesignPackage).Contains(NewtonsoftPackage);
    }

    private static HashSet<string> DirectDependencies(JsonDocument document)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (document.RootElement.TryGetProperty("projectFileDependencyGroups", out var groups))
        {
            foreach (var group in groups.EnumerateObject())
            {
                foreach (var entry in group.Value.EnumerateArray())
                {
                    roots.Add((entry.GetString() ?? string.Empty).Split(' ')[0]);
                }
            }
        }

        return roots;
    }

    private static Dictionary<string, HashSet<string>> DependencyGraph(JsonDocument document)
    {
        var graph = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in document.RootElement.GetProperty("targets").EnumerateObject())
        {
            foreach (var library in target.Value.EnumerateObject())
            {
                var name = library.Name.Split('/')[0];
                if (!graph.TryGetValue(name, out var edges))
                {
                    edges = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    graph[name] = edges;
                }

                if (library.Value.TryGetProperty("dependencies", out var dependencies))
                {
                    edges.UnionWith(dependencies.EnumerateObject().Select(dependency => dependency.Name));
                }
            }
        }

        return graph;
    }

    private static HashSet<string> Reachable(HashSet<string> roots, Dictionary<string, HashSet<string>> graph, string? skip)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>(roots.Where(root => !root.Equals(skip, StringComparison.OrdinalIgnoreCase)));
        while (pending.Count > 0)
        {
            var name = pending.Pop();
            if (!seen.Add(name) || !graph.TryGetValue(name, out var edges))
            {
                continue;
            }

            foreach (var next in edges.Where(edge => !edge.Equals(skip, StringComparison.OrdinalIgnoreCase)))
            {
                pending.Push(next);
            }
        }

        return seen;
    }

    // System.Console in any spelling; or Console followed by a member, unless Console is itself a
    // member (LogFormat.Console); or `using static Console`.
    [GeneratedRegex(@"\bSystem\s*\.\s*Console\b|(?<![.\w])Console\s*\.|\busing\s+static\s+Console\b")]
    private static partial Regex ConsolePattern();
}
