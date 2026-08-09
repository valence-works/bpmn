using System.Xml.Linq;
using Bpmn.Model;
using Shouldly;
using Xunit;

namespace Bpmn.Interchange.Tests;

/// <summary>
/// The OMG MIWG reference models, run end to end. These are real files from real tools, which is the only
/// honest test of an interchange layer: hand-written fixtures agree with whatever the author assumed.
/// <para>
/// Two properties are asserted per file. <b>Nothing is lost</b>: the element and flow counts that came out
/// of the first read come back out of the second. And <b>the writer is deterministic</b>: the second and
/// third generations are byte-identical. The first write may normalize - ordering, prefixes, layout it had
/// to synthesize - but after that the output must stop moving, or a consumer cannot tell a real edit from
/// reserialization noise in a diff.
/// </para>
/// <para>
/// Dropped and degraded findings are expected here and are not asserted against: the corpus deliberately
/// covers constructs this library does not model, and reporting them is the correct behavior.
/// </para>
/// </summary>
public sealed class CorpusRoundTripTests
{
    private readonly BpmnXmlReader _reader = new();
    private readonly BpmnXmlWriter _writer = new();

    public static TheoryData<string> CorpusFiles()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(TestPaths.CorpusDirectory, "*.bpmn").OrderBy(path => path, StringComparer.Ordinal))
            data.Add(Path.GetFileName(file));
        return data;
    }

    [Fact]
    public void The_corpus_is_present()
    {
        Directory.Exists(TestPaths.CorpusDirectory).ShouldBeTrue($"Expected the reference models at {TestPaths.CorpusDirectory}");
        Directory.GetFiles(TestPaths.CorpusDirectory, "*.bpmn").Length.ShouldBe(21, customMessage: "The MIWG reference set is 21 models.");
    }

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void The_writer_is_deterministic(string fileName)
    {
        var (_, secondGeneration, _, thirdGeneration) = RoundTrip(fileName);

        // Compared as text on purpose. Every other assertion in this suite is structural; this one exists
        // precisely to catch differences that are only positional.
        thirdGeneration.ShouldBe(secondGeneration,
            $"{fileName} keeps changing after the first write, so its output cannot be diffed.");
    }

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void A_round_trip_loses_no_elements_or_flows(string fileName)
    {
        var (first, _, second, _) = RoundTrip(fileName);

        Count(second, process => process.Elements.Count).ShouldBe(Count(first, process => process.Elements.Count), customMessage: $"{fileName} lost elements");
        Count(second, process => process.SequenceFlows.Count).ShouldBe(Count(first, process => process.SequenceFlows.Count), customMessage: $"{fileName} lost sequence flows");
        Count(second, process => process.Lanes.Count).ShouldBe(Count(first, process => process.Lanes.Count), customMessage: $"{fileName} lost lanes");
        (second.Definitions.Collaboration?.Pools.Count).ShouldBe(first.Definitions.Collaboration?.Pools.Count, customMessage: $"{fileName} lost pools");
        second.Bindings.Count.ShouldBe(first.Bindings.Count, customMessage: $"{fileName} lost work bindings");
    }

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void Retained_vendor_content_survives(string fileName)
    {
        var (first, _, second, _) = RoundTrip(fileName);

        static IEnumerable<(string Id, int Documentation, int Extensions, int Attributes, int Children)> Retention(BpmnImportResult result) =>
            result.Definitions.Processes
                .SelectMany(process => process.Elements)
                .Where(element => !element.Extensions.IsEmpty)
                .OrderBy(element => element.ElementId, StringComparer.Ordinal)
                .Select(element => (
                    element.ElementId,
                    element.Extensions.Documentation.Count,
                    element.Extensions.ExtensionElements.Count,
                    element.Extensions.ForeignAttributes.Count,
                    element.Extensions.ForeignChildren.Count));

        Retention(second).ShouldBe(Retention(first), $"{fileName} lost retained vendor content");
    }

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void Analyze_agrees_with_read(string fileName)
    {
        var xml = File.ReadAllText(Path.Combine(TestPaths.CorpusDirectory, fileName));

        _reader.Analyze(xml).Issues.Select(Describe).ShouldBe(_reader.Read(xml).Analysis.Issues.Select(Describe));
    }

    /// <summary>
    /// The claim behind normalizing diagram order: a hand-arranged layout comes out looking identical.
    /// Position in the file moves; geometry does not. Every shape's bounds, every edge's waypoints, and
    /// every label box that the source document carried must come back out bit for bit.
    /// <para>
    /// Compared as parsed numbers rather than as text, because <c>180.0</c> and <c>180</c> are the same
    /// coordinate and only one of them is worth failing a build over.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void A_hand_arranged_layout_comes_back_unchanged(string fileName)
    {
        var original = XDocument.Parse(File.ReadAllText(Path.Combine(TestPaths.CorpusDirectory, fileName)));
        var written = XDocument.Parse(_writer.Write(_reader.Read(File.ReadAllText(Path.Combine(TestPaths.CorpusDirectory, fileName)))));

        var before = Geometry(original);
        var after = Geometry(written);

        foreach (var (reference, geometry) in before)
        {
            after.ShouldContainKey(reference, customMessage: $"{fileName}: {reference} lost its layout entirely");
            after[reference].ShouldBe(geometry, customMessage: $"{fileName}: {reference} moved");
        }
    }

    /// <summary>Every laid-out thing in a document, keyed by what it draws: bounds, waypoints, and label box.</summary>
    private static Dictionary<string, string> Geometry(XDocument document)
    {
        XNamespace di = "http://www.omg.org/spec/BPMN/20100524/DI";
        XNamespace dc = "http://www.omg.org/spec/DD/20100524/DC";
        XNamespace dd = "http://www.omg.org/spec/DD/20100524/DI";

        static string Box(XElement? bounds) =>
            bounds is null
                ? "-"
                : string.Join(",", new[] { "x", "y", "width", "height" }
                    .Select(name => ((double?)bounds.Attribute(name) ?? 0).ToString("R", System.Globalization.CultureInfo.InvariantCulture)));

        var geometry = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var shape in document.Descendants(di + "BPMNShape"))
        {
            if ((string?)shape.Attribute("bpmnElement") is not { Length: > 0 } reference) continue;
            geometry[$"shape:{reference}"] =
                $"{Box(shape.Element(dc + "Bounds"))}|label={Box(shape.Element(di + "BPMNLabel")?.Element(dc + "Bounds"))}";
        }

        foreach (var edge in document.Descendants(di + "BPMNEdge"))
        {
            if ((string?)edge.Attribute("bpmnElement") is not { Length: > 0 } reference) continue;
            var waypoints = edge.Elements(dd + "waypoint")
                .Select(point => $"({(double?)point.Attribute("x") ?? 0},{(double?)point.Attribute("y") ?? 0})");

            // An edge the source left with fewer than two waypoints is not valid BPMN DI and is rerouted
            // on purpose, so it is not held to the unchanged-geometry promise.
            if (edge.Elements(dd + "waypoint").Count() < 2) continue;
            geometry[$"edge:{reference}"] = $"{string.Join("", waypoints)}|label={Box(edge.Element(di + "BPMNLabel")?.Element(dc + "Bounds"))}";
        }

        return geometry;
    }

    private static (string Severity, string Message, string? ElementId, string? ProcessId) Describe(BpmnImportIssue issue) =>
        (issue.Severity.ToString(), issue.Message, issue.ElementId, issue.ProcessId);

    private static int Count(BpmnImportResult result, Func<BpmnProcessDefinition, int> selector) =>
        result.Definitions.Processes.Sum(selector);

    private (BpmnImportResult First, string SecondGeneration, BpmnImportResult Second, string ThirdGeneration) RoundTrip(string fileName)
    {
        var first = _reader.Read(File.ReadAllText(Path.Combine(TestPaths.CorpusDirectory, fileName)));
        var secondGeneration = _writer.Write(first);
        var second = _reader.Read(secondGeneration);
        return (first, secondGeneration, second, _writer.Write(second));
    }
}
