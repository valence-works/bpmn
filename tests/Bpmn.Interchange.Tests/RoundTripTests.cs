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
            <bpmn:participant id="Pool" name="Orders" processRef="P" camunda:owner="fulfilment">
              <bpmn:extensionElements><camunda:properties><camunda:property name="sla" value="2d" /></camunda:properties></bpmn:extensionElements>
            </bpmn:participant>
            <bpmn:participant id="Customer" name="Customer" />
            <bpmn:messageFlow id="MF" sourceRef="Customer" targetRef="Await" camunda:channel="email">
              <bpmn:extensionElements><camunda:properties><camunda:property name="retries" value="3" /></camunda:properties></bpmn:extensionElements>
            </bpmn:messageFlow>
          </bpmn:collaboration>
          <bpmn:process id="P" name="Orders" isExecutable="true">
            <bpmn:documentation>Handles an order.</bpmn:documentation>
            <bpmn:laneSet id="Lanes">
              <bpmn:lane id="Lane_Ops" name="Operations" camunda:team="ops">
                <bpmn:extensionElements><camunda:properties><camunda:property name="shift" value="day" /></camunda:properties></bpmn:extensionElements>
                <bpmn:flowNodeRef>Start</bpmn:flowNodeRef>
                <bpmn:flowNodeRef>Review</bpmn:flowNodeRef>
              </bpmn:lane>
            </bpmn:laneSet>
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

    /// <summary>
    /// Participants, lanes, and message flows are the last three places vendor content can hang, and a
    /// <c>camunda:</c> attribute on a participant is a real thing modelers write. "Content-lossless" has to
    /// mean all of it, so each of the three is checked at both generations.
    /// </summary>
    [Fact]
    public void Vendor_content_on_a_participant_a_lane_and_a_message_flow_survives_a_round_trip()
    {
        var (first, firstXml, second, _) = RoundTrip();

        foreach (var generation in new[] { first, second })
        {
            var collaboration = generation.Definitions.Collaboration.ShouldNotBeNull();

            var pool = collaboration.Pools.Single(candidate => candidate.PoolId == "Pool");
            pool.Extensions.ForeignAttributes.ShouldHaveSingleItem().Name.LocalName.ShouldBe("owner");
            pool.Extensions.ForeignAttributes[0].Value.ShouldBe("fulfilment");
            pool.Extensions.ExtensionElements.ShouldHaveSingleItem().Name.LocalName.ShouldBe("properties");

            var lane = Process(generation).Lanes.Single(candidate => candidate.LaneId == "Lane_Ops");
            lane.Extensions.ForeignAttributes.ShouldHaveSingleItem().Value.ShouldBe("ops");
            lane.Extensions.ExtensionElements.ShouldHaveSingleItem().Name.LocalName.ShouldBe("properties");

            var messageFlow = collaboration.MessageFlows.Single(candidate => candidate.FlowId == "MF");
            messageFlow.Extensions.ForeignAttributes.ShouldHaveSingleItem().Value.ShouldBe("email");
            messageFlow.Extensions.ExtensionElements.ShouldHaveSingleItem().Name.LocalName.ShouldBe("properties");
        }

        // A black-box participant retains nothing here, and must not pick anything up from its neighbour.
        second.Definitions.Collaboration!.Pools.Single(pool => pool.PoolId == "Customer").Extensions.IsEmpty.ShouldBeTrue();
        firstXml.ShouldContain("camunda:owner");
        firstXml.ShouldContain("camunda:team");
        firstXml.ShouldContain("camunda:channel");
    }

    [Fact]
    public void A_lane_keeps_its_members_and_its_pool_after_a_round_trip()
    {
        var (_, _, second, _) = RoundTrip();
        var lane = Process(second).Lanes.Single();

        lane.Name.ShouldBe("Operations");
        lane.PoolId.ShouldBe("Pool");
        Process(second).Elements.Where(element => element.LaneId == "Lane_Ops")
            .Select(element => element.ElementId).ShouldBe(["Start", "Review"]);
    }

    [Fact]
    public void The_vendor_namespace_names_no_host()
    {
        BpmnXmlNames.VendorNamespaceName.ShouldBe("https://bpmn.valenceworks.io/schema/bpmn");
        BpmnXmlNames.VendorPrefix.ShouldBe("vw");
    }

    // -----------------------------------------------------------------------------------------------
    // Multi-instance on a subprocess (#21)
    // -----------------------------------------------------------------------------------------------

    /// <summary>
    /// A subprocess is both a container and an activity. Its
    /// <c>multiInstanceLoopCharacteristics</c> belong to the activity, but they sit among the nested
    /// body's children. The reader has to interpret them without also treating them as leftover
    /// content of that body, or each write-read cycle grows another copy.
    /// </summary>
    private const string MultiInstanceSubProcess = """
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" id="Defs">
          <bpmn:process id="P" isExecutable="true">
            <bpmn:startEvent id="Start" />
            <bpmn:subProcess id="Batch">
              <bpmn:startEvent id="Inner_Start" />
              <bpmn:endEvent id="Inner_Done" />
              <bpmn:sequenceFlow id="Inner_F" sourceRef="Inner_Start" targetRef="Inner_Done" />
              <bpmn:multiInstanceLoopCharacteristics isSequential="false">
                <bpmn:loopCardinality>3</bpmn:loopCardinality>
              </bpmn:multiInstanceLoopCharacteristics>
            </bpmn:subProcess>
            <bpmn:endEvent id="Done" />
            <bpmn:sequenceFlow id="F1" sourceRef="Start" targetRef="Batch" />
            <bpmn:sequenceFlow id="F2" sourceRef="Batch" targetRef="Done" />
          </bpmn:process>
        </bpmn:definitions>
        """;

    /// <summary>Same shape, with the collection bound on the parent process rather than a literal cardinality.</summary>
    private const string CollectionMultiInstanceSubProcess = $$"""
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                          xmlns:vw="{{BpmnXmlNames.VendorNamespaceName}}" id="Defs">
          <bpmn:process id="P" isExecutable="true">
            <bpmn:extensionElements>
              <vw:variable name="items" typeHint="string[]" />
            </bpmn:extensionElements>
            <bpmn:startEvent id="Start" />
            <bpmn:subProcess id="Batch">
              <bpmn:startEvent id="Inner_Start" />
              <bpmn:endEvent id="Inner_Done" />
              <bpmn:sequenceFlow id="Inner_F" sourceRef="Inner_Start" targetRef="Inner_Done" />
              <bpmn:multiInstanceLoopCharacteristics isSequential="true" vw:collection="items" vw:itemVariable="line" />
            </bpmn:subProcess>
            <bpmn:endEvent id="Done" />
            <bpmn:sequenceFlow id="F1" sourceRef="Start" targetRef="Batch" />
            <bpmn:sequenceFlow id="F2" sourceRef="Batch" targetRef="Done" />
          </bpmn:process>
        </bpmn:definitions>
        """;

    /// <summary>
    /// A Camunda-authored collection the model cannot represent. The marker must stay on the nested
    /// body as retained content, once, rather than being reported as dropped.
    /// </summary>
    private const string DegradedMultiInstanceSubProcess = """
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                          xmlns:camunda="http://camunda.org/schema/1.0/bpmn" id="Defs">
          <bpmn:process id="P" isExecutable="true">
            <bpmn:startEvent id="Start" />
            <bpmn:subProcess id="Batch">
              <bpmn:startEvent id="Inner_Start" />
              <bpmn:endEvent id="Inner_Done" />
              <bpmn:sequenceFlow id="Inner_F" sourceRef="Inner_Start" targetRef="Inner_Done" />
              <bpmn:multiInstanceLoopCharacteristics isSequential="true"
                                                     camunda:collection="items"
                                                     camunda:elementVariable="item" />
            </bpmn:subProcess>
            <bpmn:endEvent id="Done" />
            <bpmn:sequenceFlow id="F1" sourceRef="Start" targetRef="Batch" />
            <bpmn:sequenceFlow id="F2" sourceRef="Batch" targetRef="Done" />
          </bpmn:process>
        </bpmn:definitions>
        """;

    private static int MultiInstanceMarkersOn(string xml, string subprocessId) =>
        XDocument.Parse(xml).Descendants()
            .Single(element => element.Name.LocalName == "subProcess" && (string?)element.Attribute("id") == subprocessId)
            .Elements()
            .Count(child => child.Name.LocalName == "multiInstanceLoopCharacteristics");

    private static BpmnProcessDefinition NestedBody(BpmnImportResult result, string elementId) =>
        result.Bindings.OfType<BpmnWorkBinding.NestedProcess>().Single(binding => binding.ElementId == elementId).Definition;

    [Fact]
    public void A_subprocess_multi_instance_marker_is_read_onto_the_activity()
    {
        var read = Read(MultiInstanceSubProcess);

        var loop = Process(read).Elements.Single(element => element.ElementId == "Batch").LoopCharacteristics.ShouldNotBeNull();
        loop.IsSequential.ShouldBeFalse();
        loop.Cardinality.ShouldBe(3);

        read.Analysis.Issues.ShouldNotContain(issue =>
            issue.Severity == BpmnImportIssueSeverity.Dropped
            && issue.Message.Contains("multiInstanceLoopCharacteristics", StringComparison.Ordinal));
    }

    [Fact]
    public void An_interpreted_subprocess_marker_is_not_kept_on_the_nested_body()
    {
        var read = Read(MultiInstanceSubProcess);

        NestedBody(read, "Batch").Extensions.ForeignChildren
            .ShouldNotContain(child => child.Element.Name.LocalName == "multiInstanceLoopCharacteristics");
    }

    [Fact]
    public void A_collection_subprocess_marker_uses_the_parent_declared_variable()
    {
        var read = Read(CollectionMultiInstanceSubProcess);

        var loop = Process(read).Elements.Single(element => element.ElementId == "Batch").LoopCharacteristics.ShouldNotBeNull();
        loop.IsSequential.ShouldBeTrue();
        loop.CollectionVariable.ShouldBe("items");
        loop.ItemVariable.ShouldBe("line");

        NestedBody(read, "Batch").Extensions.ForeignChildren
            .ShouldNotContain(child => child.Element.Name.LocalName == "multiInstanceLoopCharacteristics");
        read.Analysis.Issues.ShouldNotContain(issue => issue.Severity == BpmnImportIssueSeverity.Dropped);
    }

    /// <summary>
    /// The write-read fixed point the host save path depends on: writing a second generation must not
    /// grow another marker, and must not change the document.
    /// </summary>
    [Theory]
    [InlineData(MultiInstanceSubProcess)]
    [InlineData(CollectionMultiInstanceSubProcess)]
    public void A_subprocess_multi_instance_round_trip_is_a_fixed_point(string xml)
    {
        var first = Read(xml);
        var firstXml = _writer.Write(first);
        var second = Read(firstXml);
        var secondXml = _writer.Write(second);

        MultiInstanceMarkersOn(firstXml, "Batch").ShouldBe(1);
        MultiInstanceMarkersOn(secondXml, "Batch").ShouldBe(1);
        secondXml.ShouldBe(firstXml);

        var firstLoop = Process(first).Elements.Single(element => element.ElementId == "Batch").LoopCharacteristics.ShouldNotBeNull();
        var secondLoop = Process(second).Elements.Single(element => element.ElementId == "Batch").LoopCharacteristics.ShouldNotBeNull();
        secondLoop.ShouldBe(firstLoop);

        first.Analysis.Issues.ShouldNotContain(issue => issue.Severity == BpmnImportIssueSeverity.Dropped);
        second.Analysis.Issues.ShouldNotContain(issue => issue.Severity == BpmnImportIssueSeverity.Dropped);
    }

    [Fact]
    public void A_degraded_subprocess_marker_is_retained_once()
    {
        var first = Read(DegradedMultiInstanceSubProcess);

        Process(first).Elements.Single(element => element.ElementId == "Batch").LoopCharacteristics.ShouldBeNull();
        first.Analysis.Issues.ShouldContain(issue =>
            issue.Severity == BpmnImportIssueSeverity.Degraded
            && issue.ElementId == "Batch"
            && issue.Message.Contains("http://camunda.org/schema/1.0/bpmn", StringComparison.Ordinal));
        first.Analysis.Issues.ShouldNotContain(issue => issue.Severity == BpmnImportIssueSeverity.Dropped);

        var retained = NestedBody(first, "Batch").Extensions.ForeignChildren
            .Where(child => child.Element.Name.LocalName == "multiInstanceLoopCharacteristics")
            .ShouldHaveSingleItem();
        retained.Element.Attributes.ShouldContain(attribute =>
            attribute.Name.LocalName == "collection"
            && attribute.Name.Namespace == "http://camunda.org/schema/1.0/bpmn"
            && attribute.Value == "items");

        var firstXml = _writer.Write(first);
        var secondXml = _writer.Write(Read(firstXml));

        MultiInstanceMarkersOn(firstXml, "Batch").ShouldBe(1);
        MultiInstanceMarkersOn(secondXml, "Batch").ShouldBe(1);
        secondXml.ShouldBe(firstXml);
    }

    // -----------------------------------------------------------------------------------------------
    // conditionExpression retention (#30)
    // -----------------------------------------------------------------------------------------------

    /// <summary>
    /// A document authored in another tool: one flow carries only a <c>conditionExpression</c>, the
    /// other carries both that and <c>vw:conditionOutcome</c>. The writer used to drop the first and
    /// replace the second with <c>outcome == '...'</c>.
    /// </summary>
    private const string ConditionalDocument = """
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                          xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                          xmlns:vw="https://bpmn.valenceworks.io/schema/bpmn"
                          xmlns:triso="http://www.trisotech.com/2015/triso"
                          id="Defs" targetNamespace="http://example.test/orders">
          <bpmn:process id="P" isExecutable="true">
            <bpmn:exclusiveGateway id="Decide" />
            <bpmn:userTask id="Approve" />
            <bpmn:userTask id="Reject" />
            <bpmn:endEvent id="Done" />
            <bpmn:sequenceFlow id="toApprove" sourceRef="Decide" targetRef="Approve" vw:conditionOutcome="Approved">
              <bpmn:conditionExpression language="http://www.w3.org/1999/XPath"
                                        xsi:type="tFormalExpression"
                                        triso:unparsed="false">amount &lt; 1000</bpmn:conditionExpression>
            </bpmn:sequenceFlow>
            <bpmn:sequenceFlow id="toReject" sourceRef="Decide" targetRef="Reject">
              <bpmn:conditionExpression language="https://www.omg.org/spec/DMN/20191111/FEEL/"
                                        xsi:type="tFormalExpression">amount &gt;= 1000</bpmn:conditionExpression>
            </bpmn:sequenceFlow>
            <bpmn:sequenceFlow id="fromApprove" sourceRef="Approve" targetRef="Done" />
          </bpmn:process>
        </bpmn:definitions>
        """;

    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";
    private static readonly XNamespace Triso = "http://www.trisotech.com/2015/triso";

    private static XElement WrittenExpression(string xml, string flowId)
    {
        var flow = XDocument.Parse(xml).Descendants()
            .Single(element => element.Name.LocalName == "sequenceFlow" && (string?)element.Attribute("id") == flowId);
        var expressions = flow.Elements().Where(element => element.Name.LocalName == "conditionExpression").ToArray();
        expressions.Length.ShouldBe(1, customMessage: $"{flowId} must write exactly one conditionExpression");
        return expressions[0];
    }

    /// <summary>
    /// The author's expression, read into the model. It must not also be retained as a foreign child, or
    /// the writer would have two to choose from.
    /// </summary>
    private static BpmnConditionExpression ModeledExpression(BpmnImportResult result, string flowId)
    {
        var flow = Process(result).SequenceFlows.Single(candidate => candidate.FlowId == flowId);
        flow.Extensions.ForeignChildren.ShouldNotContain(foreign => foreign.Element.Name.LocalName == "conditionExpression");
        return flow.ConditionExpression.ShouldNotBeNull();
    }

    private static string? Attribute(BpmnExtensionElement element, string? ns, string localName) =>
        Attribute(element.Attributes, ns, localName);

    private static string? Attribute(BpmnConditionExpression expression, string? ns, string localName) =>
        Attribute(expression.Extensions.ForeignAttributes, ns, localName);

    private static string? Attribute(IEnumerable<BpmnForeignAttribute> attributes, string? ns, string localName) =>
        attributes.SingleOrDefault(attribute =>
            string.Equals(attribute.Name.LocalName, localName, StringComparison.Ordinal)
            && string.Equals(attribute.Name.Namespace, ns, StringComparison.Ordinal))?.Value;

    [Fact]
    public void An_expression_only_flow_round_trips_the_authors_conditionExpression()
    {
        var (first, firstXml, second, secondXml) = RoundTrip(ConditionalDocument);

        Process(first).SequenceFlows.Single(flow => flow.FlowId == "toReject").ConditionOutcome.ShouldBeNull();
        first.Analysis.Issues.ShouldContain(issue =>
            issue.Severity == BpmnImportIssueSeverity.Degraded
            && issue.ElementId == "toReject"
            && issue.Message.Contains("amount >= 1000", StringComparison.Ordinal));

        foreach (var generation in new[] { first, second })
        {
            var expression = ModeledExpression(generation, "toReject");
            expression.Body.ShouldBe("amount >= 1000");
            expression.Language.ShouldBe("https://www.omg.org/spec/DMN/20191111/FEEL/");
            Attribute(expression, Xsi.NamespaceName, "type").ShouldBe("tFormalExpression");
        }

        var written = WrittenExpression(firstXml, "toReject");
        written.Value.ShouldBe("amount >= 1000");
        ((string?)written.Attribute("language")).ShouldBe("https://www.omg.org/spec/DMN/20191111/FEEL/");
        ((string?)written.Attribute(Xsi + "type")).ShouldBe("tFormalExpression");
        written.Value.ShouldNotContain("outcome ==");

        secondXml.ShouldBe(firstXml);
    }

    [Fact]
    public void A_flow_with_an_outcome_and_an_expression_keeps_the_original_expression()
    {
        var (first, firstXml, second, _) = RoundTrip(ConditionalDocument);

        foreach (var generation in new[] { first, second })
        {
            var flow = Process(generation).SequenceFlows.Single(candidate => candidate.FlowId == "toApprove");
            flow.ConditionOutcome.ShouldBe("Approved");

            var expression = ModeledExpression(generation, "toApprove");
            expression.Body.ShouldBe("amount < 1000");
            expression.Language.ShouldBe("http://www.w3.org/1999/XPath");
            Attribute(expression, Xsi.NamespaceName, "type").ShouldBe("tFormalExpression");
            Attribute(expression, Triso.NamespaceName, "unparsed").ShouldBe("false");
        }

        var written = WrittenExpression(firstXml, "toApprove");
        written.Value.ShouldBe("amount < 1000");
        ((string?)written.Attribute("language")).ShouldBe("http://www.w3.org/1999/XPath");
        ((string?)written.Attribute(Xsi + "type")).ShouldBe("tFormalExpression");
        ((string?)written.Attribute(Triso + "unparsed")).ShouldBe("false");
        written.Value.ShouldNotBe("outcome == 'Approved'");

        ((string?)XDocument.Parse(firstXml).Descendants()
            .Single(element => element.Name.LocalName == "sequenceFlow" && (string?)element.Attribute("id") == "toApprove")
            .Attribute(XNamespace.Get(BpmnXmlNames.VendorNamespaceName) + "conditionOutcome"))
            .ShouldBe("Approved");
    }

    [Fact]
    public void A_code_built_outcome_still_gets_a_generated_expression_when_none_was_retained()
    {
        var xml = _writer.Write(OutcomeOnlyDefinitions("approved"));
        WrittenExpression(xml, "f1").Value.ShouldBe("outcome == 'approved'");

        var read = Read(xml);
        var flow = Process(read).SequenceFlows.Single();
        flow.ConditionOutcome.ShouldBe("approved");
        flow.Extensions.ForeignChildren.ShouldBeEmpty();
        flow.ConditionExpression.ShouldBeNull("the generated predicate is consumed, not modeled");
        read.Analysis.Issues.ShouldNotContain(issue => issue.ElementId == "f1");
    }

    [Fact]
    public void Changing_a_library_outcome_regenerates_the_conditionExpression()
    {
        var read = Read(_writer.Write(OutcomeOnlyDefinitions("approved")));
        Process(read).SequenceFlows.Single().Extensions.ForeignChildren.ShouldBeEmpty();

        var xml = _writer.Write(WithFlowOutcome(read.Definitions, "rejected"));
        WrittenExpression(xml, "f1").Value.ShouldBe("outcome == 'rejected'");
        xml.ShouldNotContain("outcome == 'approved'");
    }

    [Fact]
    public void Removing_a_library_outcome_drops_the_generated_conditionExpression()
    {
        var read = Read(_writer.Write(OutcomeOnlyDefinitions("approved")));

        var xml = _writer.Write(WithFlowOutcome(read.Definitions, conditionOutcome: null));
        var flow = XDocument.Parse(xml).Descendants()
            .Single(element => element.Name.LocalName == "sequenceFlow" && (string?)element.Attribute("id") == "f1");
        flow.Elements().ShouldNotContain(element => element.Name.LocalName == "conditionExpression");
        flow.Attributes().ShouldNotContain(attribute => attribute.Name.LocalName == "conditionOutcome");
    }

    [Fact]
    public void A_stale_generated_expression_on_the_model_is_replaced_when_the_outcome_changes()
    {
        var stale = new BpmnExtensions(ForeignChildren:
        [
            new BpmnForeignChild(
                new BpmnExtensionElement(new BpmnQName(BpmnXmlNames.Model.NamespaceName, "conditionExpression"),
                    Value: "outcome == 'approved'"),
                Index: 0)
        ]);

        var xml = _writer.Write(new BpmnDefinitions(
            Id: "built",
            Processes:
            [
                new BpmnProcessDefinition("p", Elements:
                [
                    new BpmnElement("start", BpmnElementTypes.StartEvent),
                    new BpmnElement("done", BpmnElementTypes.EndEvent)
                ], SequenceFlows:
                [
                    new BpmnSequenceFlow("f1", "start", "done", conditionOutcome: "rejected", extensions: stale)
                ])
            ]));

        WrittenExpression(xml, "f1").Value.ShouldBe("outcome == 'rejected'");
        xml.ShouldNotContain("outcome == 'approved'");
    }

    [Fact]
    public void An_author_expression_that_looks_like_the_generated_body_is_kept_when_there_is_no_outcome()
    {
        const string authored = """
            <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" id="Defs">
              <bpmn:process id="P" isExecutable="true">
                <bpmn:startEvent id="Start" />
                <bpmn:endEvent id="Done" />
                <bpmn:sequenceFlow id="F" sourceRef="Start" targetRef="Done">
                  <bpmn:conditionExpression>outcome == 'approved'</bpmn:conditionExpression>
                </bpmn:sequenceFlow>
              </bpmn:process>
            </bpmn:definitions>
            """;

        var (first, firstXml, _, _) = RoundTrip(authored);
        ModeledExpression(first, "F").Body.ShouldBe("outcome == 'approved'");
        WrittenExpression(firstXml, "F").Value.ShouldBe("outcome == 'approved'");
        Process(first).SequenceFlows.Single().ConditionOutcome.ShouldBeNull();
    }

    /// <summary>
    /// A lookalike that does not match the current outcome is kept on read (not the library
    /// predicate) and dropped on write (it has the generated shape while an outcome is still set).
    /// </summary>
    [Fact]
    public void A_lookalike_author_expression_is_kept_on_read_and_dropped_on_write()
    {
        var first = Read(MismatchedLookalike);
        Process(first).SequenceFlows.Single().ConditionOutcome.ShouldBe("Approved");
        ModeledExpression(first, "F").Body.ShouldBe("outcome == 'Rejected'");

        var xml = _writer.Write(first);
        WrittenExpression(xml, "F").Value.ShouldBe("outcome == 'Approved'");
        xml.ShouldNotContain("outcome == 'Rejected'");
    }

    /// <summary>
    /// The same mismatched lookalike survives clearing the outcome: the writer only strips
    /// generated-shaped expressions while a <c>conditionOutcome</c> is still present.
    /// </summary>
    [Fact]
    public void A_mismatched_lookalike_survives_clearing_the_outcome()
    {
        var first = Read(MismatchedLookalike);
        ModeledExpression(first, "F").Body.ShouldBe("outcome == 'Rejected'");

        var xml = _writer.Write(WithFlowOutcome(first.Definitions, conditionOutcome: null));
        WrittenExpression(xml, "F").Value.ShouldBe("outcome == 'Rejected'");
        XDocument.Parse(xml).Descendants()
            .Single(element => element.Name.LocalName == "sequenceFlow" && (string?)element.Attribute("id") == "F")
            .Attributes().ShouldNotContain(attribute => attribute.Name.LocalName == "conditionOutcome");
    }

    [Fact]
    public void A_prefixed_xsi_type_still_resolves_after_a_round_trip()
    {
        const string prefixed = """
            <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                              xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                              id="Defs" targetNamespace="http://example.test/orders">
              <bpmn:process id="P" isExecutable="true">
                <bpmn:startEvent id="Start" />
                <bpmn:endEvent id="Done" />
                <bpmn:sequenceFlow id="F" sourceRef="Start" targetRef="Done">
                  <bpmn:conditionExpression xsi:type="bpmn:tFormalExpression">amount &lt; 1000</bpmn:conditionExpression>
                </bpmn:sequenceFlow>
              </bpmn:process>
            </bpmn:definitions>
            """;

        var (first, firstXml, _, secondXml) = RoundTrip(prefixed);

        var modeled = ModeledExpression(first, "F");
        modeled.Body.ShouldBe("amount < 1000");
        modeled.Language.ShouldBeNull();
        Attribute(modeled, Xsi.NamespaceName, "type").ShouldBe("bpmn:tFormalExpression");

        var written = WrittenExpression(firstXml, "F");
        written.Value.ShouldBe("amount < 1000");
        var type = written.Attribute(Xsi + "type").ShouldNotBeNull();
        type.Value.ShouldBe("bpmn:tFormalExpression");
        var expanded = ExpandType(written, type.Value);
        expanded.Namespace.ShouldBe(BpmnXmlNames.Model.NamespaceName);
        expanded.LocalName.ShouldBe("tFormalExpression");

        secondXml.ShouldBe(firstXml);
    }

    /// <summary>
    /// A stored <c>model:</c> prefix was not bound to BPMN (capture would have rewritten it to
    /// <c>bpmn:</c>). The writer must not rebind it, or vendor content such as
    /// <c>xmlns:model="urn:vendor:model" xsi:type="model:Thing"</c> changes namespace on write.
    /// </summary>
    [Fact]
    public void A_vendor_model_xsi_type_is_not_rebound_to_the_bpmn_namespace()
    {
        const string vendorType = """
            <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                              xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                              xmlns:vendor="urn:vendor"
                              xmlns:model="urn:vendor:model"
                              id="Defs">
              <bpmn:process id="P" isExecutable="true">
                <bpmn:startEvent id="Start" />
                <bpmn:endEvent id="Done" />
                <bpmn:sequenceFlow id="F" sourceRef="Start" targetRef="Done">
                  <bpmn:extensionElements>
                    <vendor:meta xsi:type="model:Thing" />
                  </bpmn:extensionElements>
                </bpmn:sequenceFlow>
              </bpmn:process>
            </bpmn:definitions>
            """;

        var (first, firstXml, _, _) = RoundTrip(vendorType);

        var retained = FlowExtensions(first, "F").ExtensionElements.ShouldHaveSingleItem();
        Attribute(retained, Xsi.NamespaceName, "type").ShouldBe("model:Thing");

        var written = XDocument.Parse(firstXml).Descendants()
            .Single(element => element.Name.LocalName == "meta");
        var type = written.Attribute(Xsi + "type").ShouldNotBeNull();
        type.Value.ShouldBe("model:Thing");
        written.GetNamespaceOfPrefix("model").ShouldNotBe(BpmnXmlNames.Model);
    }

    /// <summary>
    /// An outcome plus a generated-shaped body that names a different outcome: kept on read,
    /// dropped on write while the outcome remains, kept if the outcome is then cleared.
    /// </summary>
    private const string MismatchedLookalike = """
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                          xmlns:vw="https://bpmn.valenceworks.io/schema/bpmn"
                          id="Defs">
          <bpmn:process id="P" isExecutable="true">
            <bpmn:startEvent id="Start" />
            <bpmn:endEvent id="Done" />
            <bpmn:sequenceFlow id="F" sourceRef="Start" targetRef="Done" vw:conditionOutcome="Approved">
              <bpmn:conditionExpression>outcome == 'Rejected'</bpmn:conditionExpression>
            </bpmn:sequenceFlow>
          </bpmn:process>
        </bpmn:definitions>
        """;

    private static BpmnDefinitions OutcomeOnlyDefinitions(string outcome) =>
        new(Id: "built", Processes:
        [
            new BpmnProcessDefinition("p", Elements:
            [
                new BpmnElement("start", BpmnElementTypes.StartEvent),
                new BpmnElement("done", BpmnElementTypes.EndEvent)
            ], SequenceFlows:
            [
                new BpmnSequenceFlow("f1", "start", "done", conditionOutcome: outcome)
            ])
        ]);

    private static BpmnDefinitions WithFlowOutcome(BpmnDefinitions definitions, string? conditionOutcome)
    {
        var process = definitions.Processes.Single();
        var flow = process.SequenceFlows.Single();
        return definitions with
        {
            Processes =
            [
                process with
                {
                    SequenceFlows =
                    [
                        new BpmnSequenceFlow(flow.FlowId, flow.SourceRef, flow.TargetRef, flow.Name, conditionOutcome, flow.IsDefault, flow.Extensions)
                        {
                            ConditionExpression = flow.ConditionExpression
                        }
                    ]
                }
            ]
        };
    }

    private static (string? Namespace, string LocalName) ExpandType(XElement scope, string qname)
    {
        var colon = qname.IndexOf(':');
        if (colon <= 0)
            return (scope.GetDefaultNamespace().NamespaceName, qname);

        var prefix = qname[..colon];
        var local = qname[(colon + 1)..];
        var ns = scope.GetNamespaceOfPrefix(prefix);
        ns.ShouldNotBeNull($"prefixed xsi:type '{qname}' must resolve in the written document");
        return (ns!.NamespaceName, local);
    }
}
