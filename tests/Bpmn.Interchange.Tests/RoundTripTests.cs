using System.Xml.Linq;
using Bpmn.Model;
using Shouldly;
using Xunit;

namespace Bpmn.Interchange.Tests;

/// <summary>
/// The promise this library makes about a read-modify-write cycle: the semantics survive, the layout
/// survives, and the vendor annotations a modeler wrote survive. These tests hold it to that.
/// </summary>
public sealed class RoundTripTests
{
    private static readonly XNamespace Di = "http://www.omg.org/spec/BPMN/20100524/DI";
    private static readonly XNamespace Dd = "http://www.omg.org/spec/DD/20100524/DI";

    private readonly BpmnXmlReader _reader = new();
    private readonly BpmnXmlWriter _writer = new();

    /// <summary>
    /// A collaboration exercising the parts that actually carry vendor content: a pool, a user task with
    /// <c>camunda:formData</c> and a <c>camunda:assignee</c> attribute, a sequence flow with a
    /// <c>camunda:executionListener</c>, a nested subprocess, a boundary timer, and a loop-back flow.
    /// </summary>
    private const string Document = """
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                          xmlns:camunda="http://camunda.org/schema/1.0/bpmn"
                          id="Defs" targetNamespace="http://example.test/orders">
          <bpmn:message id="Msg_Approve" name="Approve" />
          <bpmn:collaboration id="Collab">
            <bpmn:extensionElements>
              <camunda:properties><camunda:property name="team" value="ops" /></camunda:properties>
            </bpmn:extensionElements>
            <bpmn:participant id="Pool" name="Orders" processRef="P" />
          </bpmn:collaboration>
          <bpmn:process id="P" name="Orders" isExecutable="true">
            <bpmn:documentation>Handles an order.</bpmn:documentation>
            <bpmn:startEvent id="Start" />
            <bpmn:userTask id="Review" name="Review" camunda:assignee="alice">
              <bpmn:extensionElements>
                <camunda:formData><camunda:formField id="approved" type="boolean" /></camunda:formData>
              </bpmn:extensionElements>
            </bpmn:userTask>
            <bpmn:boundaryEvent id="Timeout" attachedToRef="Review" cancelActivity="false">
              <bpmn:timerEventDefinition><bpmn:timeDuration>PT30M</bpmn:timeDuration></bpmn:timerEventDefinition>
            </bpmn:boundaryEvent>
            <bpmn:intermediateCatchEvent id="Await">
              <bpmn:messageEventDefinition messageRef="Msg_Approve" />
            </bpmn:intermediateCatchEvent>
            <bpmn:subProcess id="Fulfil">
              <bpmn:startEvent id="Inner_Start" />
              <bpmn:serviceTask id="Ship" name="Ship">
                <bpmn:extensionElements><camunda:connector><camunda:connectorId>http</camunda:connectorId></camunda:connector></bpmn:extensionElements>
              </bpmn:serviceTask>
              <bpmn:sequenceFlow id="Inner_F" sourceRef="Inner_Start" targetRef="Ship" />
            </bpmn:subProcess>
            <bpmn:endEvent id="Done" />
            <bpmn:sequenceFlow id="F1" sourceRef="Start" targetRef="Review">
              <bpmn:extensionElements>
                <camunda:executionListener event="take" class="com.example.Audit" />
              </bpmn:extensionElements>
            </bpmn:sequenceFlow>
            <bpmn:sequenceFlow id="F2" sourceRef="Review" targetRef="Await" />
            <bpmn:sequenceFlow id="F3" sourceRef="Await" targetRef="Fulfil" />
            <bpmn:sequenceFlow id="F4" sourceRef="Fulfil" targetRef="Done" />
            <bpmn:sequenceFlow id="F_Loop" sourceRef="Timeout" targetRef="Review" />
          </bpmn:process>
        </bpmn:definitions>
        """;

    private BpmnImportResult Read(string xml) => _reader.Read(xml);

    /// <summary>Reads, writes, and reads again, returning both generations plus the XML of each.</summary>
    private (BpmnImportResult First, string FirstXml, BpmnImportResult Second, string SecondXml) RoundTrip(string xml = Document)
    {
        var first = Read(xml);
        var firstXml = _writer.Write(first);
        var second = Read(firstXml);
        return (first, firstXml, second, _writer.Write(second));
    }

    private static BpmnProcessDefinition Process(BpmnImportResult result) => result.Definitions.Processes.Single();

    private static BpmnExtensions FlowExtensions(BpmnImportResult result, string flowId) =>
        Process(result).SequenceFlows.Single(flow => flow.FlowId == flowId).Extensions;

    // -----------------------------------------------------------------------------------------------
    // Analyse-then-commit
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public void Analyze_reports_exactly_what_a_read_reports()
    {
        var dryRun = _reader.Analyze(Document);
        var real = Read(Document).Analysis;

        dryRun.Issues.Select(issue => (issue.Severity, issue.Message, issue.ElementId, issue.ProcessId))
            .ShouldBe(real.Issues.Select(issue => (issue.Severity, issue.Message, issue.ElementId, issue.ProcessId)));
        dryRun.ProcessIds.ShouldBe(real.ProcessIds);
        dryRun.ElementCounts.ShouldBe(real.ElementCounts);
    }

    [Fact]
    public void A_document_that_cannot_be_read_at_all_throws_rather_than_reporting()
    {
        Should.Throw<BpmnInterchangeException>(() => Read("<not xml"));
        Should.Throw<BpmnInterchangeException>(() => Read("<foo/>"));
        Should.Throw<BpmnInterchangeException>(() => Read("""<definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"/>"""));
        Should.Throw<BpmnInterchangeException>(() => _reader.Read(Document, new BpmnImportOptions { ProcessId = "no-such-process" }));
    }

    // -----------------------------------------------------------------------------------------------
    // Stability
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public void Writing_the_second_generation_reproduces_it_byte_for_byte()
    {
        var (_, firstXml, _, secondXml) = RoundTrip();
        secondXml.ShouldBe(firstXml);
    }

    [Fact]
    public void The_graph_survives_the_round_trip()
    {
        var (first, _, second, _) = RoundTrip();

        Process(second).Elements.Select(element => element.ElementId)
            .ShouldBe(Process(first).Elements.Select(element => element.ElementId));
        Process(second).SequenceFlows.Select(flow => (flow.FlowId, flow.SourceRef, flow.TargetRef))
            .ShouldBe(Process(first).SequenceFlows.Select(flow => (flow.FlowId, flow.SourceRef, flow.TargetRef)));
        second.Bindings.Select(binding => (binding.GetType().Name, binding.ElementId, binding.Slot))
            .ShouldBe(first.Bindings.Select(binding => (binding.GetType().Name, binding.ElementId, binding.Slot)));
    }

    // -----------------------------------------------------------------------------------------------
    // Retention, at each level the model can hold it
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public void An_execution_listener_on_a_sequence_flow_survives_a_round_trip()
    {
        var (first, firstXml, second, _) = RoundTrip();

        foreach (var generation in new[] { FlowExtensions(first, "F1"), FlowExtensions(second, "F1") })
        {
            var listener = generation.ExtensionElements.ShouldHaveSingleItem();
            listener.Name.LocalName.ShouldBe("executionListener");
            listener.Name.Namespace.ShouldBe("http://camunda.org/schema/1.0/bpmn");
            listener.Attributes.Single(attribute => attribute.Name.LocalName == "event").Value.ShouldBe("take");
            listener.Attributes.Single(attribute => attribute.Name.LocalName == "class").Value.ShouldBe("com.example.Audit");
        }

        firstXml.ShouldContain("executionListener");
        FlowExtensions(second, "F2").IsEmpty.ShouldBeTrue("a flow with no vendor content must stay empty");
    }

    [Fact]
    public void Form_data_and_a_vendor_attribute_on_a_user_task_survive_a_round_trip()
    {
        var (_, _, second, _) = RoundTrip();
        var review = Process(second).Elements.Single(element => element.ElementId == "Review");

        review.Extensions.ExtensionElements.ShouldHaveSingleItem().Name.LocalName.ShouldBe("formData");
        review.Extensions.ForeignAttributes.ShouldHaveSingleItem().Name.LocalName.ShouldBe("assignee");
        review.Extensions.ForeignAttributes[0].Value.ShouldBe("alice");
    }

    [Fact]
    public void Collaboration_extensions_survive_a_round_trip()
    {
        var (_, _, second, _) = RoundTrip();
        var collaboration = second.Definitions.Collaboration.ShouldNotBeNull();

        collaboration.Extensions.ExtensionElements.ShouldHaveSingleItem().Name.LocalName.ShouldBe("properties");
    }

    [Fact]
    public void Extensions_inside_a_nested_subprocess_survive_a_round_trip()
    {
        var (_, _, second, _) = RoundTrip();

        var nested = second.Bindings.OfType<BpmnWorkBinding.NestedProcess>().Single(binding => binding.ElementId == "Fulfil");
        var ship = nested.Definition.Elements.Single(element => element.ElementId == "Ship");
        ship.Extensions.ExtensionElements.ShouldHaveSingleItem().Name.LocalName.ShouldBe("connector");
    }

    [Fact]
    public void A_nested_process_carries_its_own_retained_content_with_no_side_channel()
    {
        var nested = Read(Document).Bindings.OfType<BpmnWorkBinding.NestedProcess>().Single();

        // Serialising the nested process on its own must lose nothing: that is the point of hanging
        // retention off the model rather than threading a parallel structure alongside it.
        System.Text.Json.JsonSerializer.Serialize(nested.Definition).ShouldContain("connector");
    }

    [Fact]
    public void A_retained_child_returns_to_the_position_it_held()
    {
        const string withForeignChild = """
            <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:acme="http://acme.test/x" id="D">
              <bpmn:process id="P" isExecutable="true">
                <bpmn:userTask id="T">
                  <bpmn:documentation>doc</bpmn:documentation>
                  <acme:hint level="high" />
                  <bpmn:extensionElements><acme:meta k="v" /></bpmn:extensionElements>
                  <bpmn:outgoing>F</bpmn:outgoing>
                  <bpmn:ioSpecification id="io" />
                </bpmn:userTask>
                <bpmn:endEvent id="E" />
                <bpmn:sequenceFlow id="F" sourceRef="T" targetRef="E" />
              </bpmn:process>
            </bpmn:definitions>
            """;

        var first = Read(withForeignChild);
        var second = Read(_writer.Write(first));

        static IEnumerable<(string Name, int Index)> Children(BpmnImportResult result) =>
            result.Definitions.Processes.Single().Elements.Single(element => element.ElementId == "T")
                .Extensions.ForeignChildren.Select(child => (child.Element.Name.LocalName, child.Index));

        Children(first).ShouldBe([("hint", 1), ("ioSpecification", 4)]);
        Children(second).ShouldBe(Children(first));
    }

    [Fact]
    public void Semantic_fidelity_drops_foreign_content_and_says_so()
    {
        var semantic = _reader.Read(Document, new BpmnImportOptions { Fidelity = BpmnFidelity.Semantic });

        Process(semantic).Elements.ShouldAllBe(element => element.Extensions.IsEmpty);
        Process(semantic).SequenceFlows.ShouldAllBe(flow => flow.Extensions.IsEmpty);
        semantic.Analysis.Issues.ShouldContain(issue =>
            issue.Severity == BpmnImportIssueSeverity.Degraded && issue.Message.Contains("Discarded"));
    }

    [Fact]
    public void Lossless_fidelity_reports_one_finding_per_retained_namespace()
    {
        var retention = Read(Document).Analysis.Issues
            .Where(issue => issue.Message.StartsWith("Retained", StringComparison.Ordinal))
            .ToArray();

        retention.ShouldHaveSingleItem().Message.ShouldContain("camunda");
    }

    // -----------------------------------------------------------------------------------------------
    // Diagram interchange
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public void Every_flow_gets_an_edge_and_no_edge_has_fewer_than_two_waypoints()
    {
        var (first, firstXml, _, _) = RoundTrip();
        var edges = XDocument.Parse(firstXml).Descendants(Di + "BPMNEdge").ToArray();

        var drawn = edges.Select(edge => edge.Attribute("bpmnElement")!.Value).ToHashSet(StringComparer.Ordinal);
        foreach (var flow in Process(first).SequenceFlows)
            drawn.ShouldContain(flow.FlowId);

        // BPMN DI requires at least two waypoints and modelers reject an edge with one.
        edges.ShouldAllBe(edge => edge.Elements(Dd + "waypoint").Count() >= 2);
    }

    [Fact]
    public void Every_element_pool_and_lane_gets_a_shape()
    {
        var (first, firstXml, _, _) = RoundTrip();
        var shapes = XDocument.Parse(firstXml).Descendants(Di + "BPMNShape")
            .Select(shape => shape.Attribute("bpmnElement")!.Value)
            .ToArray();

        foreach (var element in Process(first).Elements)
            shapes.ShouldContain(element.ElementId);
        shapes.ShouldContain("Pool");
        XDocument.Parse(firstXml).Descendants(Di + "BPMNShape")
            .Single(shape => shape.Attribute("bpmnElement")!.Value == "Pool")
            .Attribute("isHorizontal")!.Value.ShouldBe("true");
    }

    [Fact]
    public void A_backwards_flow_is_routed_as_an_elbow_rather_than_a_straight_overlap()
    {
        var (_, firstXml, _, _) = RoundTrip();

        var loopBack = XDocument.Parse(firstXml).Descendants(Di + "BPMNEdge")
            .Single(edge => edge.Attribute("bpmnElement")!.Value == "F_Loop");

        // Three points, with the middle one below both endpoints, so a cycle does not render on top of
        // the forward path.
        var waypoints = loopBack.Elements(Dd + "waypoint")
            .Select(point => (X: (double)point.Attribute("x")!, Y: (double)point.Attribute("y")!))
            .ToArray();
        waypoints.Length.ShouldBe(3);
        waypoints[1].Y.ShouldBeGreaterThan(Math.Max(waypoints[0].Y, waypoints[2].Y));
    }

    // -----------------------------------------------------------------------------------------------
    // Canonical child order
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public void Children_are_written_in_the_order_the_schema_requires()
    {
        var (_, firstXml, _, _) = RoundTrip();
        var document = XDocument.Parse(firstXml);

        static string[] Children(XDocument document, string localName, string id) =>
            document.Descendants().Single(element => element.Name.LocalName == localName && (string?)element.Attribute("id") == id)
                .Elements().Select(child => child.Name.LocalName).ToArray();

        // tFlowNode is an xsd:sequence: documentation, extensionElements, incoming, outgoing, then the
        // type-specific content. Any other order is schema-invalid.
        Children(document, "userTask", "Review").ShouldBe(["extensionElements", "incoming", "incoming", "outgoing"]);
        Children(document, "process", "P")[0].ShouldBe("documentation");
        Children(document, "boundaryEvent", "Timeout").ShouldBe(["outgoing", "timerEventDefinition"]);
    }

    // -----------------------------------------------------------------------------------------------
    // Models built in code, where the detail lives on the binding rather than the event definition
    // -----------------------------------------------------------------------------------------------

    /// <summary>
    /// A model assembled in code puts a timer's duration on its <see cref="BpmnWorkBinding.TimerWait"/>,
    /// not in the event definition's property bag, because the binding is what the host acts on. The writer
    /// has to consult both, or a boundary event is written with no event definition at all and the reader
    /// drops it - along with every flow that referenced it.
    /// </summary>
    [Fact]
    public void A_timer_boundary_built_in_code_survives_write_then_read()
    {
        var definitions = new BpmnDefinitions(
            Id: "built",
            Processes: [new BpmnProcessDefinition("order-handling", Elements:
            [
                new BpmnElement("backorder", BpmnElementTypes.ServiceTask, name: "Backorder", bindingRef: "node-backorder"),
                new BpmnElement("backorder-timeout", BpmnElementTypes.BoundaryEvent, name: "3 days",
                    bindingRef: "node-backorder-timeout",
                    eventDefinitions: [new BpmnEventDefinition(BpmnEventDefinitionTypes.Timer)],
                    attachedToRef: "backorder", cancelActivity: true),
                new BpmnElement("cancelled", BpmnElementTypes.EndEvent)
            ], SequenceFlows:
            [
                new BpmnSequenceFlow("flow-backorder-timeout-cancelled", "backorder-timeout", "cancelled")
            ])]);

        var bindings = new BpmnWorkBinding[]
        {
            new BpmnWorkBinding.UnboundTask("order-handling", "backorder", "node-backorder", BpmnBindingSlot.Primary, BpmnElementTypes.ServiceTask),
            new BpmnWorkBinding.TimerWait("order-handling", "backorder-timeout", "node-backorder-timeout", BpmnBindingSlot.Primary, "P3D")
        };

        var xml = _writer.Write(definitions, bindings);
        xml.ShouldContain("timerEventDefinition");
        xml.ShouldContain("<timeDuration>P3D</timeDuration>");

        var read = Read(xml);
        read.Analysis.Issues.ShouldNotContain(issue => issue.Severity == BpmnImportIssueSeverity.Dropped);

        var boundary = Process(read).Elements.Single(element => element.ElementId == "backorder-timeout");
        boundary.EventDefinitions.ShouldHaveSingleItem().Type.ShouldBe(BpmnEventDefinitionTypes.Timer);
        boundary.AttachedToRef.ShouldBe("backorder");
        Process(read).SequenceFlows.ShouldHaveSingleItem().FlowId.ShouldBe("flow-backorder-timeout-cancelled");
        read.Bindings.OfType<BpmnWorkBinding.TimerWait>().ShouldHaveSingleItem().IsoDuration.ShouldBe("P3D");
    }

    /// <summary>The same gap, for the other things a binding can carry: message and signal names.</summary>
    [Theory]
    [InlineData("message")]
    [InlineData("signal")]
    public void A_catch_event_built_in_code_takes_its_name_from_the_binding(string kind)
    {
        var isMessage = kind == "message";
        var definitions = new BpmnDefinitions(
            Id: "built",
            Processes: [new BpmnProcessDefinition("p", Elements:
            [
                new BpmnElement("await", BpmnElementTypes.IntermediateCatchEvent, bindingRef: "node-await",
                    eventDefinitions: [new BpmnEventDefinition(isMessage ? BpmnEventDefinitionTypes.Message : BpmnEventDefinitionTypes.Signal)]),
                new BpmnElement("done", BpmnElementTypes.EndEvent)
            ], SequenceFlows: [new BpmnSequenceFlow("f", "await", "done")])]);

        BpmnWorkBinding binding = isMessage
            ? new BpmnWorkBinding.MessageWait("p", "await", "node-await", BpmnBindingSlot.Primary, "Approved")
            : new BpmnWorkBinding.SignalWait("p", "await", "node-await", BpmnBindingSlot.Primary, "Approved");

        var read = Read(_writer.Write(definitions, [binding]));

        read.Analysis.Issues.ShouldNotContain(issue => issue.Severity == BpmnImportIssueSeverity.Dropped);
        var definition = Process(read).Elements.Single(element => element.ElementId == "await").EventDefinitions.ShouldHaveSingleItem();
        definition.Type.ShouldBe(isMessage ? BpmnEventDefinitionTypes.Message : BpmnEventDefinitionTypes.Signal);
        definition.Properties[BpmnEventDefinitionProperties.Name].ShouldBe("Approved");
        Process(read).SequenceFlows.ShouldHaveSingleItem();
    }

    /// <summary>
    /// <c>cancelActivity</c> is omitted when interrupting, because BPMN defaults it to true. It must be
    /// written when non-interrupting: losing it would silently turn a boundary that runs alongside its
    /// activity into one that kills it.
    /// </summary>
    [Fact]
    public void A_non_interrupting_boundary_stays_non_interrupting()
    {
        var (_, firstXml, second, _) = RoundTrip();

        firstXml.ShouldContain("cancelActivity=\"false\"");
        second.Definitions.Processes.Single().Elements
            .Single(element => element.ElementId == "Timeout").CancelActivity.ShouldBeFalse();
    }

    [Fact]
    public void The_vendor_namespace_names_no_host()
    {
        BpmnXmlNames.VendorNamespaceName.ShouldBe("https://bpmn.valenceworks.io/schema/bpmn");
        BpmnXmlNames.VendorPrefix.ShouldBe("vw");
    }
}
