using Bpmn.Interchange;
using Bpmn.Model;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Bpmn.Conformance.Tests;

/// <summary>
/// Runs the reader and writer over the BPMN MIWG reference models - the corpus the OMG's Model
/// Interchange Working Group built specifically to expose interchange defects.
/// <para>
/// This is an external oracle. Every other test in this repository asserts that the library agrees with
/// itself; these files were written by a standards body with no knowledge of this implementation, which
/// is exactly what makes them worth running.
/// </para>
/// <para>
/// A dropped or degraded finding here is <b>not</b> automatically a defect. The corpus exercises the
/// whole specification, and this library implements a large subset of it. What <i>is</i> a defect: losing
/// content across a round trip, drifting between generations, or throwing on a valid document. Those are
/// asserted; coverage is merely reported.
/// </para>
/// </summary>
public sealed class MiwgCorpusTests(ITestOutputHelper output)
{
    private static readonly string CorpusDirectory = LocateCorpus();

    private readonly BpmnXmlReader _reader = new();
    private readonly BpmnXmlWriter _writer = new();

    public static TheoryData<string> CorpusFiles()
    {
        var data = new TheoryData<string>();

        foreach (var file in Directory.GetFiles(CorpusDirectory, "*.bpmn").OrderBy(f => f, StringComparer.Ordinal))
            data.Add(Path.GetFileName(file));

        return data;
    }

    [Fact]
    public void The_corpus_is_present()
    {
        // A conformance suite that silently finds no files is worse than no suite: it reports green.
        Directory.GetFiles(CorpusDirectory, "*.bpmn").Length.ShouldBeGreaterThanOrEqualTo(20,
            $"Expected the MIWG reference models under {CorpusDirectory}.");
    }

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void Every_reference_model_reads(string fileName)
    {
        var result = Read(fileName);

        result.Definitions.Processes.ShouldNotBeEmpty($"{fileName} declared no process.");
    }

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void Reading_twice_agrees_with_itself(string fileName)
    {
        // Analyze and Read share one code path by design, so a dry run can never disagree with the real
        // one. This is the test that keeps that true.
        var xml = File.ReadAllText(Path.Combine(CorpusDirectory, fileName));

        var analysis = _reader.Analyze(xml);
        var read = _reader.Read(xml);

        analysis.Issues.Count.ShouldBe(read.Analysis.Issues.Count);
        analysis.ProcessIds.ShouldBe(read.Analysis.ProcessIds);
    }

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void A_round_trip_loses_no_elements_or_flows(string fileName)
    {
        var first = Read(fileName);
        var second = _reader.Read(_writer.Write(first));

        CountElements(second).ShouldBe(CountElements(first), $"{fileName} lost elements across a round trip.");
        CountFlows(second).ShouldBe(CountFlows(first), $"{fileName} lost sequence flows across a round trip.");
    }

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void Every_emitted_edge_has_at_least_two_waypoints(string fileName)
    {
        // BPMN DI requires two or more, and modeling tools reject an edge with one. A synthesized route
        // must therefore never be a single point.
        var written = _writer.Write(Read(fileName));
        var reread = _reader.Read(written);

        foreach (var diagram in reread.Definitions.Diagrams)
        foreach (var edge in diagram.Plane.Edges)
            edge.Waypoints.Count.ShouldBeGreaterThanOrEqualTo(2,
                $"{fileName}: edge for '{edge.BpmnElementRef}' has {edge.Waypoints.Count} waypoint(s).");
    }

    /// <summary>
    /// Reports coverage rather than asserting it. Which constructs this library carries is a fact worth
    /// publishing and watching move; it is not a pass/fail condition.
    /// </summary>
    [Fact]
    public void Coverage_report()
    {
        var totals = new Dictionary<BpmnImportIssueSeverity, int>();
        var droppedConstructs = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var retainedNamespaces = new SortedSet<string>(StringComparer.Ordinal);

        var files = Directory.GetFiles(CorpusDirectory, "*.bpmn").OrderBy(f => f, StringComparer.Ordinal).ToArray();

        foreach (var file in files)
        {
            var result = _reader.Read(File.ReadAllText(file));

            foreach (var issue in result.Analysis.Issues)
            {
                totals[issue.Severity] = totals.GetValueOrDefault(issue.Severity) + 1;

                if (issue.Severity == BpmnImportIssueSeverity.Dropped)
                {
                    var construct = FirstQuotedOrLeadingWords(issue.Message);
                    droppedConstructs[construct] = droppedConstructs.GetValueOrDefault(construct) + 1;
                }
            }

            foreach (var ns in result.Definitions.Processes.SelectMany(p => p.Elements).SelectMany(e => e.Extensions.RetainedNamespaces()))
                retainedNamespaces.Add(ns);
        }

        output.WriteLine($"MIWG corpus: {files.Length} reference models");
        output.WriteLine("");
        foreach (var severity in Enum.GetValues<BpmnImportIssueSeverity>())
            output.WriteLine($"  {severity,-9} {totals.GetValueOrDefault(severity)}");

        output.WriteLine("");
        output.WriteLine("Most frequently dropped:");
        foreach (var (construct, count) in droppedConstructs.OrderByDescending(e => e.Value).Take(12))
            output.WriteLine($"  {count,4}  {construct}");

        output.WriteLine("");
        output.WriteLine("Vendor namespaces retained:");
        foreach (var ns in retainedNamespaces) output.WriteLine($"  {ns}");

        files.ShouldNotBeEmpty();
    }

    private BpmnImportResult Read(string fileName) =>
        _reader.Read(File.ReadAllText(Path.Combine(CorpusDirectory, fileName)));

    private static int CountElements(BpmnImportResult result) =>
        result.Definitions.Processes.Sum(p => p.Elements.Count);

    private static int CountFlows(BpmnImportResult result) =>
        result.Definitions.Processes.Sum(p => p.SequenceFlows.Count);

    /// <summary>Groups findings by the construct they are about, for the coverage histogram.</summary>
    private static string FirstQuotedOrLeadingWords(string message)
    {
        var quote = message.IndexOf('\'');
        var lead = quote > 0 ? message[..quote].Trim() : message;
        var words = lead.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Take(3));
    }

    private static string LocateCorpus()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "corpus")))
        {
            if (File.Exists(Path.Combine(directory.FullName, "Bpmn.slnx")))
                return Path.Combine(directory.FullName, "tests", "Bpmn.Conformance.Tests", "corpus");

            directory = directory.Parent;
        }

        return directory is null
            ? Path.Combine(AppContext.BaseDirectory, "corpus")
            : Path.Combine(directory.FullName, "corpus");
    }
}
