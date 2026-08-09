using Shouldly;

namespace Bpmn.Interchange.Tests;

/// <summary>Locates repository files from the test output directory.</summary>
internal static class TestPaths
{
    /// <summary>The repository root, found by walking up from the test binaries to the solution file.</summary>
    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    /// <summary>The vendored OMG MIWG reference models.</summary>
    public static string CorpusDirectory { get; } =
        Path.Combine(RepositoryRoot, "tests", "Bpmn.Conformance.Tests", "corpus");

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Bpmn.slnx")))
            directory = directory.Parent;

        directory.ShouldNotBeNull("Could not locate the repository root (no Bpmn.slnx found above the test output directory).");
        return directory!.FullName;
    }
}
