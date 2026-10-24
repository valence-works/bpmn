using System.Text.Json;
using Bpmn.Model;
using Shouldly;
using Xunit;

namespace Bpmn.Model.Tests;

/// <summary>
/// The model is a published, versioned on-disk format, not just an in-memory convenience. These tests
/// pin the parts of that contract a careless edit would break: wire names, defaults, and round-tripping.
/// </summary>
public sealed class BpmnModelTests
{
    [Fact]
    public void An_element_requires_an_id_and_a_type()
    {
        Should.Throw<ArgumentException>(() => new BpmnElement(" ", BpmnElementTypes.Task));
        Should.Throw<ArgumentException>(() => new BpmnElement("task-1", " "));
    }

    [Fact]
    public void Blank_optional_strings_normalize_to_null()
    {
        var element = new BpmnElement("task-1", BpmnElementTypes.Task, name: "  ", bindingRef: "   ");

        element.Name.ShouldBeNull();
        element.BindingRef.ShouldBeNull();
    }

    [Fact]
    public void Optional_strings_are_trimmed()
    {
        var element = new BpmnElement("task-1", BpmnElementTypes.Task, name: "  Approve  ", bindingRef: " node-task-1 ");

        element.Name.ShouldBe("Approve");
        element.BindingRef.ShouldBe("node-task-1");
    }

    [Fact]
    public void Boundary_events_interrupt_by_default_as_bpmn_specifies()
    {
        var boundary = new BpmnElement("boundary-1", BpmnElementTypes.BoundaryEvent, attachedToRef: "task-1");

        boundary.CancelActivity.ShouldBeTrue();
    }

    [Fact]
    public void An_element_round_trips_through_json_under_its_published_wire_names()
    {
        var element = new BpmnElement(
            "task-1",
            BpmnElementTypes.ServiceTask,
            name: "Charge card",
            bindingRef: "node-task-1",
            listenerBindingRef: "node-listener-1",
            eventDefinitions: [new BpmnEventDefinition(BpmnEventDefinitionTypes.Timer)]);

        var json = JsonSerializer.Serialize(element);

        json.ShouldContain("\"elementId\":\"task-1\"");
        json.ShouldContain("\"bindingRef\":\"node-task-1\"");
        json.ShouldContain("\"listenerBindingRef\":\"node-listener-1\"");

        var restored = JsonSerializer.Deserialize<BpmnElement>(json);

        restored.ShouldNotBeNull();
        restored!.ElementId.ShouldBe("task-1");
        restored.ElementType.ShouldBe(BpmnElementTypes.ServiceTask);
        restored.BindingRef.ShouldBe("node-task-1");
        restored.ListenerBindingRef.ShouldBe("node-listener-1");
        restored.EventDefinitions.Count.ShouldBe(1);
    }

    [Fact]
    public void A_process_definition_defaults_every_collection_to_empty()
    {
        var process = new BpmnProcessDefinition("process-1");

        process.Elements.ShouldBeEmpty();
        process.SequenceFlows.ShouldBeEmpty();
        process.Lanes.ShouldBeEmpty();
        process.Variables.ShouldBeEmpty();
        process.Extensions.ShouldBe(BpmnExtensions.Empty);
        process.IsExecutable.ShouldBeTrue();
    }

    [Fact]
    public void Definitions_find_a_process_by_id()
    {
        var definitions = new BpmnDefinitions(Processes:
        [
            new BpmnProcessDefinition("a"),
            new BpmnProcessDefinition("b")
        ]);

        definitions.FindProcess("b").ShouldNotBeNull();
        definitions.FindProcess("missing").ShouldBeNull();
    }

    [Fact]
    public void A_whole_document_round_trips_through_json()
    {
        var original = new BpmnDefinitions(
            Id: "defs-1",
            TargetNamespace: "urn:example",
            Processes:
            [
                new BpmnProcessDefinition(
                    "process-1",
                    Name: "Order",
                    Elements:
                    [
                        new BpmnElement("start-1", BpmnElementTypes.StartEvent),
                        new BpmnElement("task-1", BpmnElementTypes.UserTask, bindingRef: "node-task-1"),
                        new BpmnElement("end-1", BpmnElementTypes.EndEvent)
                    ],
                    SequenceFlows:
                    [
                        new BpmnSequenceFlow("flow-1", "start-1", "task-1"),
                        new BpmnSequenceFlow("flow-2", "task-1", "end-1")
                    ],
                    Variables: [new BpmnVariableDeclaration("orderId", "string")])
            ],
            Errors: [new BpmnErrorDeclaration("error-1", "Payment failed", "PAYMENT_FAILED")]);

        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<BpmnDefinitions>(json);

        restored.ShouldNotBeNull();
        restored!.Processes.Count.ShouldBe(1);
        restored.Processes[0].Elements.Count.ShouldBe(3);
        restored.Processes[0].SequenceFlows.Count.ShouldBe(2);
        restored.Processes[0].Variables[0].Name.ShouldBe("orderId");
        restored.Errors[0].ErrorCode.ShouldBe("PAYMENT_FAILED");
    }

    [Fact]
    public void A_condition_expression_round_trips_through_json_under_its_published_wire_names()
    {
        var flow = new BpmnSequenceFlow("flow-1", "gate", "ship")
        {
            ConditionExpression = new BpmnConditionExpression(
                " amount > 10 ",
                "urn:example:language",
                new BpmnExtensions(ForeignAttributes: [new BpmnForeignAttribute(new BpmnQName(null, "id"), "condition-1")]))
        };

        var node = JsonSerializer.SerializeToNode(flow)!.AsObject();
        var expression = node["conditionExpression"]!.AsObject();
        expression["body"]!.GetValue<string>().ShouldBe(" amount > 10 ", "the body is kept exactly as authored");
        expression["language"]!.GetValue<string>().ShouldBe("urn:example:language");
        expression["extensions"]!["foreignAttributes"]!.AsArray().Count.ShouldBe(1);

        var restored = JsonSerializer.Deserialize<BpmnSequenceFlow>(node.ToJsonString()).ShouldNotBeNull();
        restored.ConditionExpression.ShouldBe(flow.ConditionExpression, "retained content compares by value");
    }

    [Fact]
    public void A_script_round_trips_through_json_under_its_published_wire_names()
    {
        var element = new BpmnElement("script-1", BpmnElementTypes.ScriptTask)
        {
            Script = new BpmnScript(
                " total = 1 ",
                "text/javascript",
                new BpmnExtensions(ForeignAttributes: [new BpmnForeignAttribute(new BpmnQName("urn:vendor", "engine"), "nashorn")]))
        };

        var node = JsonSerializer.SerializeToNode(element)!.AsObject();
        var script = node["script"]!.AsObject();
        script["body"]!.GetValue<string>().ShouldBe(" total = 1 ", "the body is kept exactly as authored");
        script["format"]!.GetValue<string>().ShouldBe("text/javascript");
        script["extensions"]!["foreignAttributes"]!.AsArray().Count.ShouldBe(1);

        var restored = JsonSerializer.Deserialize<BpmnElement>(node.ToJsonString()).ShouldNotBeNull();
        restored.Script.ShouldBe(element.Script, "retained content compares by value");
    }

    [Fact]
    public void An_element_without_a_script_reads_from_json_with_none()
    {
        var restored = JsonSerializer.Deserialize<BpmnElement>("{\"elementId\":\"t\",\"elementType\":\"scriptTask\"}").ShouldNotBeNull();

        restored.Script.ShouldBeNull();
    }

    [Fact]
    public void A_script_requires_a_body_and_normalizes_a_blank_format()
    {
        Should.Throw<ArgumentNullException>(() => new BpmnScript(null!));

        var script = new BpmnScript("", "  ");
        script.Body.ShouldBe("");
        script.Format.ShouldBeNull();
        script.Extensions.ShouldBe(BpmnExtensions.Empty);
        new BpmnScript("x", " text/javascript ").Format.ShouldBe("text/javascript");
    }

    [Fact]
    public void Marking_a_default_flow_from_a_script_task_keeps_its_script()
    {
        var script = new BpmnScript("x = 1", "text/javascript");

        var process = new BpmnProcessBuilder("p")
            .StartEvent("start")
            .Element(new BpmnElement("run", BpmnElementTypes.ScriptTask, bindingRef: "run") { Script = script })
            .EndEvent("done")
            .Connect("start", "run")
            .Connect("run", "done", isDefault: true)
            .Build();

        var run = process.Elements.Single(element => element.ElementId == "run");
        run.DefaultFlowId.ShouldNotBeNull();
        run.Script.ShouldBe(script);
    }

    [Fact]
    public void A_condition_expression_requires_a_body_and_normalizes_a_blank_language()
    {
        Should.Throw<ArgumentNullException>(() => new BpmnConditionExpression(null!));

        var expression = new BpmnConditionExpression("", "  ");
        expression.Body.ShouldBe("");
        expression.Language.ShouldBeNull();
        expression.Extensions.ShouldBe(BpmnExtensions.Empty);
        new BpmnConditionExpression("x", " urn:example ").Language.ShouldBe("urn:example");
    }

    [Fact]
    public void The_expression_language_round_trips_on_the_definitions_and_the_process()
    {
        var original = new BpmnDefinitions(Processes: [new BpmnProcessDefinition("p") { ExpressionLanguage = "urn:example" }])
        {
            ExpressionLanguage = "urn:example"
        };

        var node = JsonSerializer.SerializeToNode(original)!.AsObject();
        node["expressionLanguage"]!.GetValue<string>().ShouldBe("urn:example");
        node["processes"]![0]!["expressionLanguage"]!.GetValue<string>().ShouldBe("urn:example");

        var restored = JsonSerializer.Deserialize<BpmnDefinitions>(node.ToJsonString()).ShouldNotBeNull();
        restored.ExpressionLanguage.ShouldBe("urn:example");
        restored.Processes.Single().ExpressionLanguage.ShouldBe("urn:example");
    }

    [Fact]
    public void A_format_1_2_document_reads_with_no_expressions_and_no_language()
    {
        const string json = """
            {
              "id": "defs",
              "processes": [
                {
                  "processId": "p",
                  "sequenceFlows": [ { "flowId": "f", "sourceRef": "a", "targetRef": "b", "conditionOutcome": "ok" } ]
                }
              ]
            }
            """;

        var restored = JsonSerializer.Deserialize<BpmnDefinitions>(json).ShouldNotBeNull();

        restored.ExpressionLanguage.ShouldBeNull();
        restored.Processes.Single().ExpressionLanguage.ShouldBeNull();
        var flow = restored.Processes.Single().SequenceFlows.Single();
        flow.ConditionOutcome.ShouldBe("ok");
        flow.ConditionExpression.ShouldBeNull();
    }

    [Fact]
    public void An_error_declaration_carries_the_code_bpmn_matches_on()
    {
        // BPMN matches a thrown error against catching events by errorCode, not by name or id.
        var declaration = new BpmnErrorDeclaration("error-1", "Payment failed", "PAYMENT_FAILED");

        declaration.ErrorCode.ShouldBe("PAYMENT_FAILED");
    }

    [Fact]
    public void Values_distinguish_absent_from_null_from_stored_externally()
    {
        BpmnValue.Absent.Presence.ShouldBe(BpmnValuePresence.Absent);
        BpmnValue.Absent.HasValue.ShouldBeFalse();

        BpmnValue.Null.Presence.ShouldBe(BpmnValuePresence.Null);
        BpmnValue.Null.HasValue.ShouldBeFalse();

        BpmnValue.FromInteger(3).HasValue.ShouldBeTrue();
        BpmnValue.FromInteger(3).TypeHint.ShouldBe(BpmnValueTypes.Integer);

        // A value the host holds but did not supply inline is unreadable, not missing. Treating it as
        // absent would silently iterate a collection-mode multi-instance activity zero times.
        new BpmnValue(BpmnValuePresence.StoredExternally).HasValue.ShouldBeFalse();
    }
}
