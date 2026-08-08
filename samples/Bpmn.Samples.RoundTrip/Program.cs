using Bpmn.Interchange;
using Bpmn.Model;

// Reads a BPMN file, writes it back, and reports what survived.
//
// The comparison is STRUCTURAL, not textual. Formatting is explicitly not preserved - attribute order,
// whitespace, comments and namespace prefix choices all move - so a textual diff would be noise. What
// must survive is content: elements, flows, variables, layout, and the vendor annotations other readers
// discard.
//
// This doubles as a lint tool. Point it at a directory and it tells you which files this library can
// carry losslessly and which it cannot.

var nonFlagArgs = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
var target = nonFlagArgs.FirstOrDefault() ?? LocateCorpus();

var files = Directory.Exists(target)
    ? Directory.GetFiles(target, "*.bpmn").OrderBy(f => f, StringComparer.Ordinal).ToArray()
    : File.Exists(target) ? [target] : [];

if (files.Length == 0)
{
    Console.Error.WriteLine($"No .bpmn files at {target}");
    return 1;
}

var reader = new BpmnXmlReader();
var writer = new BpmnXmlWriter();

Console.WriteLine($"{"file",-22} {"elem",5} {"flow",5} {"ext",4} {"stable",7}  findings");
Console.WriteLine(new string('-', 78));

var unstable = 0;
var failed = 0;

foreach (var file in files)
{
    var name = Path.GetFileName(file);

    try
    {
        var first = reader.Read(File.ReadAllText(file));
        var writtenOnce = writer.Write(first);

        // Second generation. If a read-write cycle is stable, generation two and generation three are
        // identical - the first pass may normalize, but nothing should keep drifting after that.
        var second = reader.Read(writtenOnce);
        var writtenTwice = writer.Write(second);

        var stable = string.Equals(writtenOnce, writtenTwice, StringComparison.Ordinal);
        if (!stable) unstable++;

        var elements = first.Definitions.Processes.Sum(p => p.Elements.Count);
        var flows = first.Definitions.Processes.Sum(p => p.SequenceFlows.Count);

        var elementsAfter = second.Definitions.Processes.Sum(p => p.Elements.Count);
        var flowsAfter = second.Definitions.Processes.Sum(p => p.SequenceFlows.Count);

        var retained = first.Definitions.Processes
            .SelectMany(p => p.Elements)
            .Count(e => !e.Extensions.IsEmpty);

        var lost = new List<string>();
        if (elementsAfter != elements) lost.Add($"elements {elements}->{elementsAfter}");
        if (flowsAfter != flows) lost.Add($"flows {flows}->{flowsAfter}");

        var dropped = first.Analysis.Issues.Count(i => i.Severity == BpmnImportIssueSeverity.Dropped);
        var degraded = first.Analysis.Issues.Count(i => i.Severity == BpmnImportIssueSeverity.Degraded);

        var findings = new List<string>();
        if (dropped > 0) findings.Add($"{dropped} dropped");
        if (degraded > 0) findings.Add($"{degraded} degraded");
        findings.AddRange(lost);

        Console.WriteLine(
            $"{Truncate(name, 22),-22} {elements,5} {flows,5} {retained,4} {(stable ? "yes" : "NO"),7}  " +
            (findings.Count == 0 ? "clean" : string.Join(", ", findings)));

        if (lost.Count > 0) failed++;
    }
    catch (BpmnInterchangeException exception)
    {
        Console.WriteLine($"{Truncate(name, 22),-22} {"",5} {"",5} {"",4} {"-",7}  REJECTED: {exception.Message}");
        failed++;
    }
}

Console.WriteLine();
Console.WriteLine($"{files.Length} file(s): {failed} lost content, {unstable} unstable across generations");
Console.WriteLine();
Console.WriteLine("Dropped and degraded findings are not necessarily defects here - a construct this");
Console.WriteLine("library does not implement is reported honestly rather than silently mangled.");
Console.WriteLine("Content loss across a round trip, and instability between generations, are defects.");

return failed == 0 && unstable == 0 ? 0 : 1;

static string Truncate(string value, int length) =>
    value.Length <= length ? value : value[..(length - 1)] + "…";

static string LocateCorpus()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);

    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Bpmn.slnx")))
        directory = directory.Parent;

    return directory is null
        ? AppContext.BaseDirectory
        : Path.Combine(directory.FullName, "tests", "Bpmn.Conformance.Tests", "corpus");
}
