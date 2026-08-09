using Shouldly;
using Xunit;

namespace Bpmn.Interchange.Tests;

/// <summary>
/// Guards the guards.
/// <para>
/// This repository enforces host-agnosticism by searching source files for a forbidden token. Some of
/// those searches run as shell <c>grep</c>, and <c>grep</c> treats any file containing a NUL byte as
/// binary and skips it without a word. A single stray <c>\0</c> in a string literal is therefore enough
/// to make a source file invisible to the check while still reporting a clean pass - and it would hide
/// exactly the files most likely to carry text ported in from elsewhere.
/// </para>
/// <para>
/// A guard that can be blinded by its own input is worse than no guard, because a pass reads as proof.
/// This test costs a directory walk and removes the failure mode.
/// </para>
/// </summary>
public sealed class SourceFileHygieneTests
{
    private static readonly string[] SearchRoots = ["src", "tests", "samples"];

    private static readonly HashSet<string> TextExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".cs", ".csproj", ".props", ".targets", ".md", ".json", ".bpmn", ".xml", ".slnx" };

    private readonly string _repositoryRoot = TestPaths.RepositoryRoot;

    [Fact]
    public void No_source_file_contains_a_nul_byte()
    {
        var offenders = new List<string>();

        foreach (var file in TextFiles())
        {
            var index = Array.IndexOf(File.ReadAllBytes(file), (byte)0);
            if (index >= 0)
                offenders.Add($"{Path.GetRelativePath(_repositoryRoot, file)} (byte offset {index})");
        }

        offenders.ShouldBeEmpty(
            "A NUL byte makes a text file look binary, so grep-based checks skip it silently and report a pass. " +
            "Offending files: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// The same walk, asserting the files are readable as text at all. A file that cannot be decoded is
    /// equally invisible to a line-oriented check.
    /// </summary>
    [Fact]
    public void Every_source_file_is_readable_as_text()
    {
        var offenders = new List<string>();

        foreach (var file in TextFiles())
        {
            try
            {
                _ = File.ReadAllText(file);
            }
            catch (Exception exception)
            {
                offenders.Add($"{Path.GetRelativePath(_repositoryRoot, file)}: {exception.Message}");
            }
        }

        offenders.ShouldBeEmpty("Every tracked source file must decode as text. Offending files: " + string.Join(", ", offenders));
    }

    private IEnumerable<string> TextFiles()
    {
        foreach (var folder in SearchRoots)
        {
            var root = Path.Combine(_repositoryRoot, folder);
            if (!Directory.Exists(root)) continue;

            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (!TextExtensions.Contains(Path.GetExtension(file))) continue;
                if (IsBuildOutput(file)) continue;
                yield return file;
            }
        }
    }

    private static bool IsBuildOutput(string file) =>
        file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
}
