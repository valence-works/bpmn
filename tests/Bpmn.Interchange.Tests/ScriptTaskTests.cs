using System.Text.Json;
using System.Xml.Linq;
using Bpmn.Model;
using Shouldly;
using Xunit;

namespace Bpmn.Interchange.Tests;

/// <summary>
/// A script task's <c>scriptFormat</c> and <c>&lt;script&gt;</c> are modeled as <see cref="BpmnElement.Script"/>,
/// not retained as foreign XML or dropped. These tests hold the reader and writer to a lossless round trip of
/// both. The library never runs a script; that stays the host's.
/// </summary>
public sealed class ScriptTaskTests
{
    private const string JavaScript = "text/javascript";
    private static readonly XNamespace Model = BpmnXmlNames.Model;

    private readonly BpmnXmlReader _reader = new();
    private readonly BpmnXmlWriter _writer = new();

    private static string TaskDocument(string task) => $"""
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                          xmlns:v="urn:vendor:scripts"
                          id="Defs" targetNamespace="http://example.test/orders">
          <bpmn:process id="P" isExecutable="true">
            <bpmn:startEvent id="Start" />
            {task}
            <bpmn:endEvent id="Done" />
            <bpmn:sequenceFlow id="toRun" sourceRef="Start" targetRef="Run" />
            <bpmn:sequenceFlow id="toDone" sourceRef="Run" targetRef="Done" />
          </bpmn:process>
        </bpmn:definitions>
        """;

    private static BpmnElement Run(BpmnImportResult result) =>
        result.Definitions.Processes.Single().Elements.Single(element => element.ElementId == "Run");

    private static XElement WrittenTask(string xml) =>
        XDocument.Parse(xml).Descendants(Model + "scriptTask").Single(element => (string?)element.Attribute("id") == "Run");

    private static string Semantic(BpmnImportResult result) =>
        JsonSerializer.Serialize(result.Definitions.Processes) + JsonSerializer.Serialize(result.Bindings);

    [Fact]
    public void The_format_and_body_are_read_onto_the_element_and_nothing_is_retained_as_foreign()
    {
        var read = _reader.Read(TaskDocument("""
            <bpmn:scriptTask id="Run" scriptFormat="text/javascript">
              <bpmn:script>total = 1</bpmn:script>
            </bpmn:scriptTask>
            """));

        var run = Run(read);
        var script = run.Script.ShouldNotBeNull();
        script.Format.ShouldBe(JavaScript);
        script.Body.ShouldBe("total = 1");
        script.Extensions.IsEmpty.ShouldBeTrue();
        run.Extensions.ForeignChildren.ShouldBeEmpty("the script is modeled, not retained");
        run.Extensions.ForeignAttributes.ShouldBeEmpty();
    }

    [Fact]
    public void A_cdata_body_reads_verbatim_and_is_written_back_as_the_same_text()
    {
        var read = _reader.Read(TaskDocument("""
            <bpmn:scriptTask id="Run" scriptFormat="text/javascript">
              <bpmn:script><![CDATA[if (a < 1 && b > 2) { c = "x"; }]]></bpmn:script>
            </bpmn:scriptTask>
            """));

        Run(read).Script.ShouldNotBeNull().Body.ShouldBe("if (a < 1 && b > 2) { c = \"x\"; }");
        WrittenTask(_writer.Write(read)).Element(Model + "script").ShouldNotBeNull().Value
            .ShouldBe("if (a < 1 && b > 2) { c = \"x\"; }");
    }

    [Fact]
    public void A_format_without_a_script_reads_with_an_empty_body_and_writes_no_script_element()
    {
        var read = _reader.Read(TaskDocument("""<bpmn:scriptTask id="Run" scriptFormat="groovy" />"""));

        var script = Run(read).Script.ShouldNotBeNull();
        script.Format.ShouldBe("groovy");
        script.Body.ShouldBe("");

        var written = WrittenTask(_writer.Write(read));
        ((string?)written.Attribute("scriptFormat")).ShouldBe("groovy");
        written.Element(Model + "script").ShouldBeNull();
        Run(_reader.Read(_writer.Write(read))).Script.ShouldBe(script);
    }

    [Theory]
    [InlineData("""<bpmn:scriptTask id="Run" />""")]
    [InlineData("""<bpmn:scriptTask id="Run"><bpmn:script /></bpmn:scriptTask>""")]
    public void A_task_with_neither_a_format_nor_a_script_body_has_no_script(string task)
    {
        var read = _reader.Read(TaskDocument(task));

        Run(read).Script.ShouldBeNull();
        WrittenTask(_writer.Write(read)).Attribute("scriptFormat").ShouldBeNull();
    }

    [Fact]
    public void A_scripts_own_attributes_and_child_elements_are_retained_on_the_script()
    {
        var read = _reader.Read(TaskDocument("""
            <bpmn:scriptTask id="Run" scriptFormat="text/javascript" v:engine="nashorn">
              <bpmn:script v:mode="strict" resource="run.js"><v:source path="run.js" />total = 1</bpmn:script>
            </bpmn:scriptTask>
            """));

        var run = Run(read);
        var script = run.Script.ShouldNotBeNull();
        script.Body.ShouldBe("total = 1");
        script.Extensions.ForeignAttributes.Select(attribute => (attribute.Name.Namespace, attribute.Name.LocalName, attribute.Value))
            .ShouldBe([(null, "resource", "run.js"), ("urn:vendor:scripts", "mode", "strict")], ignoreOrder: true);
        script.Extensions.ForeignChildren.ShouldHaveSingleItem().Element.Name.LocalName.ShouldBe("source");
        run.Extensions.ForeignAttributes.ShouldHaveSingleItem().Name.LocalName.ShouldBe("engine", "the task keeps its own vendor attribute");

        var written = WrittenTask(_writer.Write(read)).Element(Model + "script").ShouldNotBeNull();
        ((string?)written.Attribute("resource")).ShouldBe("run.js");
        ((string?)written.Attribute(XNamespace.Get("urn:vendor:scripts") + "mode")).ShouldBe("strict");
        written.Element(XNamespace.Get("urn:vendor:scripts") + "source").ShouldNotBeNull();
    }

    [Fact]
    public void Semantic_fidelity_keeps_the_format_and_body_and_drops_the_rest()
    {
        var read = _reader.Read(
            TaskDocument("""
                <bpmn:scriptTask id="Run" scriptFormat="text/javascript">
                  <bpmn:script v:mode="strict"><v:source path="run.js" />total = 1</bpmn:script>
                </bpmn:scriptTask>
                """),
            new BpmnImportOptions { Fidelity = BpmnFidelity.Semantic });

        var script = Run(read).Script.ShouldNotBeNull();
        script.Format.ShouldBe(JavaScript);
        script.Body.ShouldBe("total = 1");
        script.Extensions.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void A_code_built_script_is_written_as_the_last_child_after_the_loop_characteristics()
    {
        var process = new BpmnProcessDefinition("p", Elements:
        [
            new BpmnElement("start", BpmnElementTypes.StartEvent),
            new BpmnElement("run", BpmnElementTypes.ScriptTask, bindingRef: "run",
                loopCharacteristics: new BpmnLoopCharacteristics(isSequential: true, cardinality: 2))
            {
                Script = new BpmnScript("total = 1", JavaScript)
            },
            new BpmnElement("done", BpmnElementTypes.EndEvent)
        ], SequenceFlows: [new BpmnSequenceFlow("f1", "start", "run"), new BpmnSequenceFlow("f2", "run", "done")]);

        var task = XDocument.Parse(_writer.Write(process)).Descendants(Model + "scriptTask").Single();

        ((string?)task.Attribute("scriptFormat")).ShouldBe(JavaScript);
        task.Elements().Select(element => element.Name.LocalName)
            .ShouldBe(["incoming", "outgoing", "multiInstanceLoopCharacteristics", "script"]);
        task.Element(Model + "script")!.Value.ShouldBe("total = 1");
    }

    [Fact]
    public void A_script_on_an_element_that_is_not_a_script_task_is_not_written()
    {
        var process = new BpmnProcessDefinition("p", Elements:
        [
            new BpmnElement("start", BpmnElementTypes.StartEvent),
            new BpmnElement("run", BpmnElementTypes.ServiceTask, bindingRef: "run") { Script = new BpmnScript("total = 1", JavaScript) },
            new BpmnElement("done", BpmnElementTypes.EndEvent)
        ], SequenceFlows: [new BpmnSequenceFlow("f1", "start", "run"), new BpmnSequenceFlow("f2", "run", "done")]);

        var task = XDocument.Parse(_writer.Write(process)).Descendants(Model + "serviceTask").Single();

        task.Attribute("scriptFormat").ShouldBeNull();
        task.Element(Model + "script").ShouldBeNull();
    }

    [Fact]
    public void A_read_write_read_gives_the_same_model_and_a_second_write_is_stable()
    {
        var source = TaskDocument("""
            <bpmn:scriptTask id="Run" scriptFormat="text/javascript" isForCompensation="false">
              <bpmn:documentation>Totals the order.</bpmn:documentation>
              <bpmn:multiInstanceLoopCharacteristics isSequential="true"><bpmn:loopCardinality>2</bpmn:loopCardinality></bpmn:multiInstanceLoopCharacteristics>
              <bpmn:script v:mode="strict"><![CDATA[total = a < b ? 1 : 2]]></bpmn:script>
            </bpmn:scriptTask>
            """);

        var first = _reader.Read(source);
        var firstXml = _writer.Write(first);
        var second = _reader.Read(firstXml);

        Semantic(second).ShouldBe(Semantic(first));
        Run(second).Script.ShouldBe(Run(first).Script);
        _writer.Write(second).ShouldBe(firstXml);
    }

    [Fact]
    public void A_script_task_in_a_nested_subprocess_body_keeps_its_script()
    {
        var read = _reader.Read(TaskDocument("""
            <bpmn:subProcess id="Run">
              <bpmn:startEvent id="InnerStart" />
              <bpmn:scriptTask id="Inner" scriptFormat="text/javascript"><bpmn:script>inner = 1</bpmn:script></bpmn:scriptTask>
              <bpmn:endEvent id="InnerDone" />
              <bpmn:sequenceFlow id="i1" sourceRef="InnerStart" targetRef="Inner" />
              <bpmn:sequenceFlow id="i2" sourceRef="Inner" targetRef="InnerDone" />
            </bpmn:subProcess>
            """));

        var body = read.Bindings.OfType<BpmnWorkBinding.NestedProcess>().Single(binding => binding.ElementId == "Run").Definition;
        var expected = new BpmnScript("inner = 1", JavaScript);
        body.Elements.Single(element => element.ElementId == "Inner").Script.ShouldBe(expected);

        var again = _reader.Read(_writer.Write(read));
        again.Bindings.OfType<BpmnWorkBinding.NestedProcess>().Single(binding => binding.ElementId == "Run").Definition
            .Elements.Single(element => element.ElementId == "Inner").Script.ShouldBe(expected);
    }

    [Fact]
    public void A_read_script_task_round_trips_through_json()
    {
        var read = _reader.Read(TaskDocument("""
            <bpmn:scriptTask id="Run" scriptFormat="text/javascript">
              <bpmn:script v:mode="strict">total = 1</bpmn:script>
            </bpmn:scriptTask>
            """));

        var json = JsonSerializer.Serialize(read.Definitions);
        var restored = JsonSerializer.Deserialize<BpmnDefinitions>(json).ShouldNotBeNull();

        restored.Processes.Single().Elements.Single(element => element.ElementId == "Run").Script.ShouldBe(Run(read).Script);
        _writer.Write(restored, read.Bindings).ShouldBe(_writer.Write(read));
    }

    [Fact]
    public void A_second_script_is_reported_as_dropped_and_not_written()
    {
        // BPMN allows one <script>. Retaining the second would not keep it either: the writer writes the modeled
        // script in place of a retained one, so it would vanish on write without a finding.
        var read = _reader.Read(TaskDocument("""
            <bpmn:scriptTask id="Run" scriptFormat="text/javascript">
              <bpmn:script>first = 1</bpmn:script>
              <bpmn:script>second = 2</bpmn:script>
            </bpmn:scriptTask>
            """));

        var run = Run(read);
        run.Script.ShouldNotBeNull().Body.ShouldBe("first = 1");
        run.Extensions.ForeignChildren.ShouldBeEmpty();
        read.Analysis.Issues.ShouldContain(issue => issue.ElementId == "Run" && issue.Severity == BpmnImportIssueSeverity.Dropped);
        WrittenTask(_writer.Write(read)).Elements(Model + "script").ShouldHaveSingleItem().Value.ShouldBe("first = 1");
    }

    [Fact]
    public void A_pre_1_6_payload_with_a_retained_script_still_writes_it_once()
    {
        // Format 1.5.0 carried a script task's script as a retained foreign child, and dropped its format.
        var retained = new BpmnExtensions(ForeignChildren:
        [
            new BpmnForeignChild(new BpmnExtensionElement(new BpmnQName(Model.NamespaceName, "script"), Value: "total = 1"), Index: 0)
        ]);
        var legacy = new BpmnElement("Run", BpmnElementTypes.ScriptTask, bindingRef: "Run", extensions: retained);
        var process = new BpmnProcessDefinition("p", Elements: [legacy], SequenceFlows: []);

        var legacyXml = _writer.Write(process);
        XDocument.Parse(legacyXml).Descendants(Model + "script").ShouldHaveSingleItem().Value.ShouldBe("total = 1");

        var upgraded = _reader.Read(legacyXml).Definitions.Processes.Single().Elements.Single();
        upgraded.Script.ShouldBe(new BpmnScript("total = 1"));
        upgraded.Extensions.ForeignChildren.ShouldBeEmpty();

        var both = new BpmnProcessDefinition("p", Elements: [new BpmnElement("Run", BpmnElementTypes.ScriptTask, bindingRef: "Run", extensions: retained) { Script = new BpmnScript("current") }], SequenceFlows: []);
        XDocument.Parse(_writer.Write(both)).Descendants(Model + "script").ShouldHaveSingleItem().Value
            .ShouldBe("current", "the modeled script wins over a retained copy");
    }
}
