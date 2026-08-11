using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Shouldly;
using Xunit;

namespace Bpmn.Architecture.Tests;

/// <summary>
/// The guard that keeps this library host-agnostic.
/// <para>
/// The promise is that the shipped packages depend on nothing but the base class library and each
/// other, and that no particular host is named, assumed, or referenced anywhere. That promise is
/// checkable, so it is checked here rather than left to review.
/// </para>
/// <para>
/// The assertions are written as a <b>positive allowlist</b>: an assembly reference is legal only if it
/// is the BCL or one of this repository's own assemblies. Naming forbidden hosts instead would only
/// catch the hosts someone happened to think of; an allowlist catches every one.
/// </para>
/// </summary>
public sealed class HostAgnosticBoundaryTests
{
    /// <summary>The four packages this repository publishes.</summary>
    private static readonly string[] ShippedAssemblies =
    [
        "Bpmn.Model",
        "Bpmn.Interchange",
        "Bpmn.Semantics",
        "Bpmn.Runtime.InMemory"
    ];

    /// <summary>
    /// Assembly-name prefixes that are part of the platform. Everything else is a third-party
    /// dependency, and a shipped package is not allowed to have one.
    /// </summary>
    private static readonly string[] PlatformPrefixes =
    [
        "System.",
        "System",
        "netstandard",
        "mscorlib",
        "Microsoft.CSharp",
        "Microsoft.VisualBasic"
    ];

    [Theory]
    [InlineData("Bpmn.Model")]
    [InlineData("Bpmn.Interchange")]
    [InlineData("Bpmn.Semantics")]
    [InlineData("Bpmn.Runtime.InMemory")]
    public void Shipped_assembly_references_only_the_platform_and_its_own_siblings(string assemblyName)
    {
        var assembly = Assembly.Load(assemblyName);

        var offending = assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => !IsPlatform(name) && !IsOwn(name))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        offending.ShouldBeEmpty(
            $"{assemblyName} must depend only on the base class library and other Bpmn.* assemblies. " +
            $"Unexpected: {string.Join(", ", offending)}");
    }

    [Fact]
    public void Shipped_projects_declare_no_external_package_references()
    {
        var repositoryRoot = FindRepositoryRoot();

        var violations = new List<string>();

        foreach (var name in ShippedAssemblies)
        {
            var projectFile = Path.Combine(repositoryRoot, "src", name, $"{name}.csproj");
            File.Exists(projectFile).ShouldBeTrue($"Expected to find {projectFile}");

            var document = XDocument.Load(projectFile);

            var packages = document
                .Descendants("PackageReference")
                .Select(element => element.Attribute("Include")?.Value ?? string.Empty)
                .Where(id => id.Length > 0)
                .ToArray();

            // A project file may legitimately carry nothing. Anything it does carry is a real dependency,
            // because build-time-only assets are declared centrally in Directory.Build.props with
            // PrivateAssets="all" and never flow to a consumer.
            foreach (var package in packages)
                violations.Add($"{name} declares PackageReference '{package}'");
        }

        violations.ShouldBeEmpty(
            "Shipped packages must carry zero external NuGet dependencies. " +
            string.Join("; ", violations));
    }

    /// <summary>
    /// The prose counterpart to the assembly checks. A reference graph cannot catch a host's name in a
    /// doc comment, an identifier, or a string literal - and those are exactly how a neutral library
    /// starts drifting back towards the codebase it came from.
    /// <para>
    /// One exception is legitimate: prose that compares this library against alternatives has to name
    /// them, or the comparison is useless. That exception is <b>scoped, not granted per file</b> -
    /// wrap the region in <c>host-agnostic-allow</c> / <c>host-agnostic-allow-end</c> markers and the
    /// rest of the same file stays checked.
    /// </para>
    /// </summary>
    [Fact]
    public void Source_names_no_specific_host()
    {
        var repositoryRoot = FindRepositoryRoot();

        var forbidden = new Regex("elsa", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        var allowStart = new Regex("host-agnostic-allow:", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        var allowEnd = new Regex("host-agnostic-allow-end", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        var searchRoots = new[] { "src", "tests", "samples", "docs", "tools" }
            .Select(folder => Path.Combine(repositoryRoot, folder))
            .Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            .Concat(new[] { "README.md", "CONTRIBUTING.md" }
                .Select(name => Path.Combine(repositoryRoot, name))
                .Where(File.Exists));

        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".csproj", ".md", ".json", ".bpmn" };

        var hits = new List<string>();

        foreach (var file in searchRoots)
        {
            if (!extensions.Contains(Path.GetExtension(file))) continue;
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;

            // This file necessarily contains the token it searches for. A lint rule excluding itself
            // is normal; the alternative is obfuscating the pattern, which hides what is enforced.
            if (Path.GetFileName(file) == "HostAgnosticBoundaryTests.cs") continue;

            var allowed = false;
            var lineNumber = 0;

            foreach (var line in File.ReadLines(file))
            {
                lineNumber++;

                if (allowStart.IsMatch(line)) { allowed = true; continue; }
                if (allowEnd.IsMatch(line)) { allowed = false; continue; }
                if (!allowed && forbidden.IsMatch(line))
                    hits.Add($"{Path.GetRelativePath(repositoryRoot, file)}:{lineNumber}");
            }

            if (allowed)
                hits.Add($"{Path.GetRelativePath(repositoryRoot, file)}: unclosed host-agnostic-allow region");
        }

        hits.ShouldBeEmpty(
            "This library must name no specific host outside a scoped host-agnostic-allow region. " +
            "Offending lines: " + string.Join(", ", hits.Take(20)));
    }

    private static bool IsPlatform(string name) =>
        PlatformPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal));

    private static bool IsOwn(string name) =>
        name.StartsWith("Bpmn.", StringComparison.Ordinal) || name == "Bpmn";

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Bpmn.slnx")))
            directory = directory.Parent;

        directory.ShouldNotBeNull("Could not locate the repository root (no Bpmn.slnx found above the test output directory).");
        return directory!.FullName;
    }
}
