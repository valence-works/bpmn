using System.Xml.Linq;
using Bpmn.Model;
using Shouldly;
using Xunit;

namespace Bpmn.Interchange.Tests;

/// <summary>Checks that the single-process entry point carries the document writer's existing behavior through.</summary>
public sealed class SingleProcessWriteTests
{
    private static readonly XNamespace Model = BpmnXmlNames.Model;
    private static readonly XNamespace Di = BpmnXmlNames.Di;
    private static readonly XNamespace Dd = BpmnXmlNames.Dd;

    private readonly BpmnXmlReader _reader = new();
    private readonly BpmnXmlWriter _writer = new();

    [Fact]
    public void A_single_process_writes_and_reads_with_synthesized_di()
    {
        var process = SimpleProcess();

        var firstXml = _writer.Write(process);
        var firstDocument = XDocument.Parse(firstXml);
        var plane = firstDocument.Descendants(Di + "BPMNPlane").ShouldHaveSingleItem();

        plane.Elements(Di + "BPMNShape").Select(shape => (string?)shape.Attribute("bpmnElement"))
            .ShouldBe(process.Elements.Select(element => element.ElementId));
        var edges = plane.Elements(Di + "BPMNEdge").ToArray();
        edges.Select(edge => (string?)edge.Attribute("bpmnElement"))
            .ShouldBe(process.SequenceFlows.Select(flow => flow.FlowId));
        edges.Select(edge => edge.Elements(Dd + "waypoint").Count()).ShouldAllBe(count => count == 2);

        var firstRead = _reader.Read(firstXml);
        var roundTrippedXml = _writer.Write(firstRead);
        var secondRead = _reader.Read(roundTrippedXml);
        var roundTrippedProcess = secondRead.Definitions.Processes.ShouldHaveSingleItem();

        roundTrippedProcess.ProcessId.ShouldBe(process.ProcessId);
        roundTrippedProcess.Elements.Select(element => element.ElementId).ShouldBe(process.Elements.Select(element => element.ElementId));
        roundTrippedProcess.SequenceFlows.Select(flow => flow.FlowId).ShouldBe(process.SequenceFlows.Select(flow => flow.FlowId));
    }

    [Fact]
    public void Bindings_supply_nested_processes_and_event_names()
    {
        var process = new BpmnProcessDefinition(
            "invoice",
            Elements:
            [
                new BpmnElement("start", BpmnElementTypes.StartEvent),
                new BpmnElement("embedded", BpmnElementTypes.SubProcess, bindingRef: "node-embedded"),
                new BpmnElement("await-approval", BpmnElementTypes.IntermediateCatchEvent,
                    bindingRef: "node-await-approval",
                    eventDefinitions: [new BpmnEventDefinition(BpmnEventDefinitionTypes.Message)]),
                new BpmnElement("done", BpmnElementTypes.EndEvent)
            ],
            SequenceFlows:
            [
                new BpmnSequenceFlow("to-embedded", "start", "embedded"),
                new BpmnSequenceFlow("to-await", "embedded", "await-approval"),
                new BpmnSequenceFlow("to-done", "await-approval", "done")
            ]);
        var nested = new BpmnProcessDefinition(
            "embedded",
            Elements:
            [
                new BpmnElement("inner-start", BpmnElementTypes.StartEvent),
                new BpmnElement("inner-work", BpmnElementTypes.ServiceTask),
                new BpmnElement("inner-done", BpmnElementTypes.EndEvent)
            ],
            SequenceFlows:
            [
                new BpmnSequenceFlow("inner-to-work", "inner-start", "inner-work"),
                new BpmnSequenceFlow("inner-to-done", "inner-work", "inner-done")
            ]);
        BpmnWorkBinding[] bindings =
        [
            new BpmnWorkBinding.NestedProcess("invoice", "embedded", "node-embedded", BpmnBindingSlot.Primary, nested),
            new BpmnWorkBinding.MessageWait("invoice", "await-approval", "node-await-approval", BpmnBindingSlot.Primary, "Invoice approved")
        ];

        var xml = _writer.Write(process, bindings);
        var document = XDocument.Parse(xml);
        var nestedXml = document.Descendants(Model + "subProcess").Single();
        nestedXml.Elements(Model + "serviceTask").Single().Attribute("id")!.Value.ShouldBe("inner-work");
        var messageDefinition = document.Descendants(Model + "messageEventDefinition").ShouldHaveSingleItem();
        var messageId = (string)messageDefinition.Attribute("messageRef")!;
        var declaration = document.Root!.Elements(Model + "message")
            .Single(message => (string?)message.Attribute("id") == messageId);
        declaration.Attribute("name")!.Value.ShouldBe("Invoice approved");

        var read = _reader.Read(xml);
        read.Bindings.OfType<BpmnWorkBinding.NestedProcess>().ShouldHaveSingleItem()
            .Definition.Elements.ShouldContain(element => element.ElementId == "inner-work");
        read.Bindings.OfType<BpmnWorkBinding.MessageWait>().ShouldHaveSingleItem()
            .MessageName.ShouldBe("Invoice approved");
    }

    [Fact]
    public void Export_options_are_applied_to_the_single_process_document()
    {
        const string vendorNamespace = "https://example.test/process-vendor";
        const string vendorPrefix = "acme";
        var process = new BpmnProcessDefinition(
            "review",
            Elements:
            [
                new BpmnElement("start", BpmnElementTypes.StartEvent),
                new BpmnElement("review-task", BpmnElementTypes.UserTask, bindingRef: "node-review-task"),
                new BpmnElement("done", BpmnElementTypes.EndEvent)
            ],
            SequenceFlows:
            [
                new BpmnSequenceFlow("to-review", "start", "review-task"),
                new BpmnSequenceFlow("approved", "review-task", "done", conditionOutcome: "approved")
            ]);
        var options = new BpmnExportOptions
        {
            TargetNamespace = "https://example.test/review",
            Exporter = "Test exporter",
            ExporterVersion = "2.4",
            VendorNamespace = vendorNamespace,
            VendorPrefix = vendorPrefix,
            OmitXmlDeclaration = true
        };

        var xml = _writer.Write(process, options: options);
        var document = XDocument.Parse(xml);
        var root = document.Root!;

        document.Declaration.ShouldBeNull();
        root.Attribute("targetNamespace")!.Value.ShouldBe(options.TargetNamespace);
        root.Attribute("exporter")!.Value.ShouldBe(options.Exporter);
        root.Attribute("exporterVersion")!.Value.ShouldBe(options.ExporterVersion);
        root.GetNamespaceOfPrefix(vendorPrefix)!.NamespaceName.ShouldBe(vendorNamespace);
        root.Descendants(Model + "sequenceFlow").Single(flow => (string?)flow.Attribute("id") == "approved")
            .Attribute(XName.Get("conditionOutcome", vendorNamespace))!.Value.ShouldBe("approved");

        var read = _reader.Read(xml, new BpmnImportOptions { VendorNamespace = vendorNamespace, VendorPrefix = vendorPrefix });
        read.Definitions.TargetNamespace.ShouldBe(options.TargetNamespace);
        read.Definitions.Exporter.ShouldBe(options.Exporter);
        read.Definitions.ExporterVersion.ShouldBe(options.ExporterVersion);
        read.Definitions.Processes.Single().SequenceFlows.Single(flow => flow.FlowId == "approved").ConditionOutcome.ShouldBe("approved");
    }

    [Fact]
    public void Event_references_get_the_existing_inferred_declarations()
    {
        var process = new BpmnProcessDefinition(
            "declarations",
            Elements:
            [
                new BpmnElement("await-message", BpmnElementTypes.IntermediateCatchEvent,
                    eventDefinitions: [new BpmnEventDefinition(BpmnEventDefinitionTypes.Message,
                        new Dictionary<string, string> { [BpmnEventDefinitionProperties.Name] = "Invoice ready" })]),
                new BpmnElement("await-signal", BpmnElementTypes.IntermediateCatchEvent,
                    eventDefinitions: [new BpmnEventDefinition(BpmnEventDefinitionTypes.Signal,
                        new Dictionary<string, string> { [BpmnEventDefinitionProperties.Name] = "Payment settled" })]),
                new BpmnElement("work", BpmnElementTypes.ServiceTask),
                new BpmnElement("missing-error-declaration", BpmnElementTypes.BoundaryEvent,
                    eventDefinitions: [new BpmnEventDefinition(BpmnEventDefinitionTypes.Error)],
                    properties: new Dictionary<string, string> { [BpmnXmlReader.ErrorRefPropertyKey] = "Error_Network" },
                    attachedToRef: "work"),
                new BpmnElement("raise-escalation", BpmnElementTypes.IntermediateThrowEvent,
                    eventDefinitions: [new BpmnEventDefinition(BpmnEventDefinitionTypes.Escalation,
                        new Dictionary<string, string>
                        {
                            [BpmnEventDefinitionProperties.Code] = "credit-limit",
                            [BpmnEventDefinitionProperties.Name] = "Credit limit reached"
                        })])
            ]);

        var xml = _writer.Write(process);
        var document = XDocument.Parse(xml);
        var root = document.Root!;
        var messageReference = (string)document.Descendants(Model + "messageEventDefinition").Single().Attribute("messageRef")!;
        var signalReference = (string)document.Descendants(Model + "signalEventDefinition").Single().Attribute("signalRef")!;
        var errorReference = (string)document.Descendants(Model + "errorEventDefinition").Single().Attribute("errorRef")!;
        var escalationReference = (string)document.Descendants(Model + "escalationEventDefinition").Single().Attribute("escalationRef")!;

        var message = root.Elements(Model + "message").Single(declaration => (string?)declaration.Attribute("id") == messageReference);
        var signal = root.Elements(Model + "signal").Single(declaration => (string?)declaration.Attribute("id") == signalReference);
        var error = root.Elements(Model + "error").Single(declaration => (string?)declaration.Attribute("id") == errorReference);
        var escalation = root.Elements(Model + "escalation").Single(declaration => (string?)declaration.Attribute("id") == escalationReference);

        message.Attribute("name")!.Value.ShouldBe("Invoice ready");
        signal.Attribute("name")!.Value.ShouldBe("Payment settled");
        error.Attribute("name").ShouldBeNull();
        error.Attribute("errorCode").ShouldBeNull();
        errorReference.ShouldBe("Error_Network");
        escalation.Attribute("name")!.Value.ShouldBe("Credit limit reached");
        escalation.Attribute("escalationCode")!.Value.ShouldBe("credit-limit");

        var read = _reader.Read(xml);
        read.Definitions.Messages.ShouldHaveSingleItem().Name.ShouldBe("Invoice ready");
        read.Definitions.Signals.ShouldHaveSingleItem().Name.ShouldBe("Payment settled");
        read.Definitions.Errors.ShouldHaveSingleItem().Id.ShouldBe("Error_Network");
        read.Definitions.Errors.Single().ErrorCode.ShouldBeNull();
        read.Definitions.Escalations.ShouldHaveSingleItem().EscalationCode.ShouldBe("credit-limit");
    }

    [Fact]
    public void A_null_typed_process_is_rejected()
    {
        Should.Throw<ArgumentNullException>(() => _writer.Write((BpmnProcessDefinition)null!))
            .ParamName.ShouldBe("process");
    }

    private static BpmnProcessDefinition SimpleProcess() => new(
        "order",
        Name: "Order",
        Elements:
        [
            new BpmnElement("start", BpmnElementTypes.StartEvent),
            new BpmnElement("review", BpmnElementTypes.UserTask, bindingRef: "node-review"),
            new BpmnElement("done", BpmnElementTypes.EndEvent)
        ],
        SequenceFlows:
        [
            new BpmnSequenceFlow("to-review", "start", "review"),
            new BpmnSequenceFlow("to-done", "review", "done")
        ]);
}
