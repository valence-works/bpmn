using System.Globalization;
using Bpmn.Interchange;
using Bpmn.Model;

// Builds a BPMN process in code and writes it out as BPMN 2.0 XML.
//
// The acceptance criterion for the output is not "it parses". It is that the file opens in a real BPMN
// modeler without complaint, laid out and readable. Layout is synthesized where the model carries none,
// and every edge gets at least two waypoints, because BPMN DI requires that and modelers reject less.

var output = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal))
             ?? Path.Combine(Path.GetTempPath(), "order-handling.bpmn");

// A small order process with a decision, a compensating path, and a timeout on the manual step.
var process = new BpmnProcessBuilder("order-handling")
    .Name("Order handling")
    .Variable("orderId", "string")
    .Variable("amount", "decimal")

    .StartEvent("received", "Order received")
    .ServiceTask("reserve", "Reserve stock")
    .ExclusiveGateway("in-stock")
    .ServiceTask("charge", "Charge card")
    .UserTask("backorder", "Handle backorder")
    .EndEvent("shipped", "Shipped")
    .EndEvent("cancelled", "Cancelled")

    // A boundary timer on the manual step. Interrupting, so if it fires the user task is terminated and
    // the token leaves down the boundary path.
    //
    // Note the split: the *element* says "there is a timer here", and the *binding* below says how long.
    // The model describes the process; bindings describe the work a host has to provide. Keeping the
    // duration out of the element is what lets a host substitute its own timer implementation.
    .BoundaryEvent(
        "backorder-timeout",
        attachedTo: "backorder",
        eventDefinition: new BpmnEventDefinition(BpmnEventDefinitionTypes.Timer),
        interrupting: true,
        name: "3 days",
        bindingRef: "node-backorder-timeout")

    .Connect("received", "reserve")
    .Connect("reserve", "in-stock")
    .Connect("in-stock", "charge", condition: "available")
    .Connect("in-stock", "backorder", isDefault: true)
    .Connect("charge", "shipped")
    .Connect("backorder", "charge")
    .Connect("backorder-timeout", "cancelled")
    .Build();

var definitions = new BpmnDefinitionsBuilder()
    .Id("order-definitions")
    .TargetNamespace("https://example.com/bpmn/order")
    .Exporter("Bpmn.Samples.Build", "0.1.0")
    .Error("out-of-stock", "Out of stock", errorCode: "OUT_OF_STOCK")
    .Process(process)
    .Build();

// The work each element needs, stated in BPMN terms. A host reads these and wires its own
// implementations to the matching BindingRef; the writer uses them to emit <timeDuration> and the
// bodies of nested subprocesses.
var bindings = new List<BpmnWorkBinding>
{
    new BpmnWorkBinding.UnboundTask("order-handling", "reserve", "node-reserve", BpmnBindingSlot.Primary, BpmnElementTypes.ServiceTask),
    new BpmnWorkBinding.UnboundTask("order-handling", "charge", "node-charge", BpmnBindingSlot.Primary, BpmnElementTypes.ServiceTask),
    new BpmnWorkBinding.UnboundTask("order-handling", "backorder", "node-backorder", BpmnBindingSlot.Primary, BpmnElementTypes.UserTask),
    new BpmnWorkBinding.TimerWait("order-handling", "backorder-timeout", "node-backorder-timeout", BpmnBindingSlot.Primary, "P3D"),
};

var xml = new BpmnXmlWriter().Write(definitions, bindings);
File.WriteAllText(output, xml);

Console.WriteLine($"Wrote {output}");
Console.WriteLine();
Console.WriteLine($"  {process.Elements.Count} elements");
Console.WriteLine($"  {process.SequenceFlows.Count} sequence flows");
Console.WriteLine($"  {process.Variables.Count} variables");
Console.WriteLine($"  {bindings.Count} work bindings");
Console.WriteLine($"  {xml.Length.ToString("N0", CultureInfo.InvariantCulture)} characters of XML");
Console.WriteLine();

// Read it straight back. If the writer produced something the reader disagrees with, that is worth
// finding here rather than in a modeler.
var reread = new BpmnXmlReader().Read(xml);
var roundTripped = reread.Definitions.Processes.Single();

var elementsMatch = roundTripped.Elements.Count == process.Elements.Count;
var flowsMatch = roundTripped.SequenceFlows.Count == process.SequenceFlows.Count;

Console.WriteLine("read back:");
Console.WriteLine($"  elements  {roundTripped.Elements.Count,3} {(elementsMatch ? "==" : "!=")} {process.Elements.Count}");
Console.WriteLine($"  flows     {roundTripped.SequenceFlows.Count,3} {(flowsMatch ? "==" : "!=")} {process.SequenceFlows.Count}");

var problems = reread.Analysis.Issues.Where(i => i.Severity != BpmnImportIssueSeverity.Info).ToArray();
Console.WriteLine($"  findings  {problems.Length} above Info");
foreach (var issue in problems) Console.WriteLine($"    [{issue.Severity}] {issue.Message}");

Console.WriteLine();
Console.WriteLine("Open the file in a BPMN modeler to check the layout.");

return elementsMatch && flowsMatch && problems.Length == 0 ? 0 : 1;
