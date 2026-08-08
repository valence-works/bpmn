using Bpmn.Interchange;
using Bpmn.Model;

// Reads a BPMN file, prints what is in it, and prints what the reader thought of it.
//
// The interesting part is the last section. Most BPMN tooling either accepts a file or rejects it; this
// reader tells you, element by element, what it understood, what it reduced, and what it could not keep
// - and it retains the vendor annotations other readers throw away.

var corpus = LocateCorpus();
var path = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal))
           ?? Path.Combine(corpus, "A.2.0.bpmn");

if (!File.Exists(path))
{
    Console.Error.WriteLine($"No such file: {path}");
    return 1;
}

Console.WriteLine($"Reading {Path.GetFileName(path)}");
Console.WriteLine(new string('-', 72));

var xml = File.ReadAllText(path);
var result = new BpmnXmlReader().Read(xml);

// -- What the document contains -------------------------------------------------------------------

foreach (var process in result.Definitions.Processes)
{
    Console.WriteLine();
    Console.WriteLine($"process {process.ProcessId}{Describe(process.Name)}");
    Console.WriteLine($"  {process.Elements.Count} elements, {process.SequenceFlows.Count} sequence flows");

    foreach (var element in process.Elements.Take(12))
    {
        var binding = element.BindingRef is null ? "" : $"  -> binds {element.BindingRef}";
        Console.WriteLine($"    {element.ElementType,-24} {element.ElementId,-20}{Describe(element.Name)}{binding}");
    }

    if (process.Elements.Count > 12)
        Console.WriteLine($"    ... and {process.Elements.Count - 12} more");
}

if (result.Definitions.Collaboration is { } collaboration && collaboration.Pools.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine($"collaboration: {collaboration.Pools.Count} pools, {collaboration.MessageFlows.Count} message flows");
}

// -- What work the host would have to provide -----------------------------------------------------

if (result.Bindings.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine($"work bindings ({result.Bindings.Count}) - what a host would have to implement:");

    foreach (var group in result.Bindings.GroupBy(b => b.GetType().Name).OrderBy(g => g.Key, StringComparer.Ordinal))
        Console.WriteLine($"  {group.Key,-18} {group.Count()}");
}

// -- Retained vendor annotations ------------------------------------------------------------------
//
// This is the part that distinguishes a lossless read. A file authored in a commercial modeler carries
// camunda:*, zeebe:* or flowable:* content that most readers silently discard.

var retained = result.Definitions.Processes
    .SelectMany(p => p.Elements)
    .Where(e => !e.Extensions.IsEmpty)
    .ToArray();

Console.WriteLine();
if (retained.Length == 0)
{
    Console.WriteLine("no vendor extensions in this document");
}
else
{
    var namespaces = retained.SelectMany(e => e.Extensions.RetainedNamespaces()).Distinct().ToArray();
    Console.WriteLine($"retained vendor content on {retained.Length} element(s), from {namespaces.Length} namespace(s):");

    foreach (var ns in namespaces) Console.WriteLine($"  {ns}");

    foreach (var element in retained.Take(5))
    {
        var names = element.Extensions.ExtensionElements.Select(x => x.Name.LocalName);
        Console.WriteLine($"  {element.ElementId}: {string.Join(", ", names)}");
    }
}

// -- What the reader thought ----------------------------------------------------------------------

var analysis = result.Analysis;

Console.WriteLine();
Console.WriteLine("element histogram:");
foreach (var (name, count) in analysis.ElementCounts.OrderByDescending(e => e.Value).Take(10))
    Console.WriteLine($"  {name,-28} {count}");

Console.WriteLine();
Console.WriteLine($"findings: {analysis.Issues.Count}");

foreach (var severity in new[] { BpmnImportIssueSeverity.Dropped, BpmnImportIssueSeverity.Degraded, BpmnImportIssueSeverity.Info })
{
    var issues = analysis.Issues.Where(i => i.Severity == severity).ToArray();
    if (issues.Length == 0) continue;

    Console.WriteLine();
    Console.WriteLine($"  {severity} ({issues.Length})");
    foreach (var issue in issues.Take(8))
        Console.WriteLine($"    [{issue.ElementId ?? "-"}] {issue.Message}");
    if (issues.Length > 8) Console.WriteLine($"    ... and {issues.Length - 8} more");
}

Console.WriteLine();
Console.WriteLine("Nothing was executed. This reader parses and reports; it never runs a process.");
return 0;

static string Describe(string? name) => string.IsNullOrWhiteSpace(name) ? "" : $"  \"{name}\"";

// The MIWG conformance corpus doubles as sample input, so the sample has something real to read
// without shipping its own fixtures.
static string LocateCorpus()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);

    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Bpmn.slnx")))
        directory = directory.Parent;

    return directory is null
        ? AppContext.BaseDirectory
        : Path.Combine(directory.FullName, "tests", "Bpmn.Conformance.Tests", "corpus");
}
