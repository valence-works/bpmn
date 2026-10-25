using System.Text.Json;
using System.Xml.Linq;
using Bpmn.Model;
using Shouldly;
using Xunit;

namespace Bpmn.Interchange.Tests;

/// <summary>
/// <c>&lt;conditionExpression&gt;</c> and <c>definitions/@expressionLanguage</c> are modeled, not retained
/// as foreign XML (ADR 0011). These tests hold the reader and writer to a lossless round trip of both, at
/// every nesting depth. An expression-only flow reads without a finding: whether a host can evaluate it is
/// a capability graph build checks.
/// </summary>
public sealed class ConditionExpressionTests
{
    private const string Feel = "https://www.omg.org/spec/DMN/20191111/FEEL/";
    private const string JavaScript = "http://www.ecma-international.org/ecma-262/5.1";
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";
    private static readonly XNamespace Vendor = "urn:vendor:expressions";

    private readonly BpmnXmlReader _reader = new();
    private readonly BpmnXmlWriter _writer = new();

    /// <summary>
    /// A document-level language, a CDATA body with markup characters, a body that declares its own
    /// language, an expression carrying an id, documentation, a vendor extension and a vendor attribute,
    /// and a subprocess whose body routes on an expression of its own.
    /// </summary>
    private const string Document = """
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                          xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                          xmlns:v="urn:vendor:expressions"
                          id="Defs" targetNamespace="http://example.test/orders"
                          expressionLanguage="https://www.omg.org/spec/DMN/20191111/FEEL/">
          <bpmn:process id="P" isExecutable="true">
            <bpmn:startEvent id="Start" />
            <bpmn:exclusiveGateway id="Decide" default="toManual" />
            <bpmn:userTask id="Approve" />
            <bpmn:userTask id="Manual" />
            <bpmn:subProcess id="Batch">
              <bpmn:startEvent id="BatchStart" />
              <bpmn:exclusiveGateway id="BatchDecide" />
              <bpmn:endEvent id="BatchSmall" />
              <bpmn:endEvent id="BatchLarge" />
              <bpmn:sequenceFlow id="toBatchDecide" sourceRef="BatchStart" targetRef="BatchDecide" />
              <bpmn:sequenceFlow id="toBatchSmall" sourceRef="BatchDecide" targetRef="BatchSmall">
                <bpmn:conditionExpression xsi:type="bpmn:tFormalExpression">count &lt; 10</bpmn:conditionExpression>
              </bpmn:sequenceFlow>
              <bpmn:sequenceFlow id="toBatchLarge" sourceRef="BatchDecide" targetRef="BatchLarge">
                <bpmn:conditionExpression xsi:type="bpmn:tFormalExpression">count &gt;= 10</bpmn:conditionExpression>
              </bpmn:sequenceFlow>
            </bpmn:subProcess>
            <bpmn:endEvent id="Done" />
            <bpmn:sequenceFlow id="toDecide" sourceRef="Start" targetRef="Decide" />
            <bpmn:sequenceFlow id="toApprove" sourceRef="Decide" targetRef="Approve">
              <bpmn:conditionExpression xsi:type="bpmn:tFormalExpression"><![CDATA[amount < 1000 && region = "EU"]]></bpmn:conditionExpression>
            </bpmn:sequenceFlow>
            <bpmn:sequenceFlow id="toBatch" sourceRef="Decide" targetRef="Batch">
              <bpmn:conditionExpression id="BatchCondition" xsi:type="tFormalExpression" language="http://www.ecma-international.org/ecma-262/5.1"
                                        evaluatesToTypeRef="xs:boolean" v:origin="imported">
                <bpmn:documentation>Large orders are batched.</bpmn:documentation>
                <bpmn:extensionElements><v:hint level="2" /></bpmn:extensionElements>amount &gt;= 1000</bpmn:conditionExpression>
            </bpmn:sequenceFlow>
            <bpmn:sequenceFlow id="toManual" sourceRef="Decide" targetRef="Manual" />
            <bpmn:sequenceFlow id="fromApprove" sourceRef="Approve" targetRef="Done" />
          </bpmn:process>
        </bpmn:definitions>
        """;

    private (BpmnImportResult First, string FirstXml, BpmnImportResult Second, string SecondXml) RoundTrip(string xml)
    {
        var first = _reader.Read(xml);
        var firstXml = _writer.Write(first);
        var second = _reader.Read(firstXml);
        var secondXml = _writer.Write(second);
        return (first, firstXml, second, secondXml);
    }

    private static BpmnProcessDefinition Process(BpmnImportResult result) => result.Definitions.Processes.Single();

    /// <summary>
    /// The semantic model a read produced, in the payload format: every process with its flows and their
    /// expressions, the document's expression language, and the bindings. Two reads that serialize the
    /// same are the same model, which is the round-trip check the guide recommends. Diagrams and root
    /// metadata are left out because a write synthesizes layout and a target namespace a hand-written
    /// source may not carry.
    /// </summary>
    private static string Model(BpmnImportResult result) =>
        JsonSerializer.Serialize(result.Definitions.Processes)
        + result.Definitions.ExpressionLanguage
        + JsonSerializer.Serialize(result.Bindings);

    private static string FlowDocument(string flowContent) => $"""
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                          xmlns:model="http://www.omg.org/spec/BPMN/20100524/MODEL"
                          xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                          xmlns:v="urn:vendor:expressions"
                          id="Defs" targetNamespace="http://example.test/orders">
          <bpmn:process id="P" isExecutable="true">
            <bpmn:startEvent id="Start" />
            <bpmn:endEvent id="Done" />
            <bpmn:sequenceFlow id="F" sourceRef="Start" targetRef="Done">{flowContent}</bpmn:sequenceFlow>
          </bpmn:process>
        </bpmn:definitions>
        """;

    private static string OutcomeFlowDocument(string conditionOutcome, string flowContent) => $"""
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                          xmlns:vw="https://bpmn.valenceworks.io/schema/bpmn"
                          id="Defs" targetNamespace="http://example.test/orders">
          <bpmn:process id="P" isExecutable="true">
            <bpmn:startEvent id="Start" />
            <bpmn:endEvent id="Done" />
            <bpmn:sequenceFlow id="F" sourceRef="Start" targetRef="Done" vw:conditionOutcome="{conditionOutcome}">{flowContent}</bpmn:sequenceFlow>
          </bpmn:process>
        </bpmn:definitions>
        """;

    private static BpmnProcessDefinition NestedBody(BpmnImportResult result, string elementId) =>
        result.Bindings.OfType<BpmnWorkBinding.NestedProcess>().Single(binding => binding.ElementId == elementId).Definition;

    private static BpmnSequenceFlow Flow(BpmnProcessDefinition process, string flowId) =>
        process.SequenceFlows.Single(flow => flow.FlowId == flowId);

    private static XElement WrittenExpression(string xml, string flowId)
    {
        var flow = XDocument.Parse(xml).Descendants()
            .Single(element => element.Name.LocalName == "sequenceFlow" && (string?)element.Attribute("id") == flowId);
        return flow.Elements().Where(element => element.Name.LocalName == "conditionExpression").ShouldHaveSingleItem();
    }

    private static string? Attribute(BpmnConditionExpression expression, string? ns, string localName) =>
        expression.Extensions.ForeignAttributes.SingleOrDefault(attribute =>
            string.Equals(attribute.Name.LocalName, localName, StringComparison.Ordinal)
            && string.Equals(attribute.Name.Namespace, ns, StringComparison.Ordinal))?.Value;

    [Fact]
    public void The_document_expression_language_is_read_onto_the_definitions_and_every_process()
    {
        var read = _reader.Read(Document);

        read.Definitions.ExpressionLanguage.ShouldBe(Feel);
        Process(read).ExpressionLanguage.ShouldBe(Feel);
        NestedBody(read, "Batch").ExpressionLanguage.ShouldBe(Feel, "a nested body is built into a graph of its own");
    }

    [Fact]
    public void A_cdata_body_reads_verbatim_and_is_written_back_as_the_same_text()
    {
        var (first, firstXml, second, _) = RoundTrip(Document);

        foreach (var generation in new[] { first, second })
        {
            var expression = Flow(Process(generation), "toApprove").ConditionExpression.ShouldNotBeNull();
            expression.Body.ShouldBe("amount < 1000 && region = \"EU\"");
            expression.Language.ShouldBeNull("it inherits the document's language rather than copying it");
        }

        WrittenExpression(firstXml, "toApprove").Value.ShouldBe("amount < 1000 && region = \"EU\"");
    }

    [Fact]
    public void An_expressions_own_language_id_type_documentation_and_vendor_content_survive()
    {
        var (first, firstXml, second, _) = RoundTrip(Document);

        foreach (var generation in new[] { first, second })
        {
            var flow = Flow(Process(generation), "toBatch");
            flow.Extensions.IsEmpty.ShouldBeTrue("the expression's content belongs to the expression, not the flow");

            var expression = flow.ConditionExpression.ShouldNotBeNull();
            expression.Body.Trim().ShouldBe("amount >= 1000");
            expression.Language.ShouldBe(JavaScript);
            Attribute(expression, null, "id").ShouldBe("BatchCondition");
            Attribute(expression, null, "evaluatesToTypeRef").ShouldBe("xs:boolean");
            Attribute(expression, Xsi.NamespaceName, "type").ShouldBe("tFormalExpression");
            Attribute(expression, Vendor.NamespaceName, "origin").ShouldBe("imported");
            expression.Extensions.Documentation.ShouldHaveSingleItem().Text.ShouldBe("Large orders are batched.");
            expression.Extensions.ExtensionElements.ShouldHaveSingleItem().Name.LocalName.ShouldBe("hint");
        }

        var written = WrittenExpression(firstXml, "toBatch");
        ((string?)written.Attribute("id")).ShouldBe("BatchCondition");
        ((string?)written.Attribute("language")).ShouldBe(JavaScript);
        ((string?)written.Attribute("evaluatesToTypeRef")).ShouldBe("xs:boolean");
        ((string?)written.Attribute(Xsi + "type")).ShouldBe("tFormalExpression");
        ((string?)written.Attribute(Vendor + "origin")).ShouldBe("imported");
        written.Elements().Select(element => element.Name.LocalName).ShouldBe(["documentation", "extensionElements"]);
    }

    [Fact]
    public void Expressions_in_a_nested_subprocess_body_are_modeled_and_round_trip()
    {
        var (first, firstXml, second, _) = RoundTrip(Document);

        foreach (var generation in new[] { first, second })
        {
            var body = NestedBody(generation, "Batch");
            Flow(body, "toBatchSmall").ConditionExpression.ShouldNotBeNull().Body.ShouldBe("count < 10");
            Flow(body, "toBatchLarge").ConditionExpression.ShouldNotBeNull().Body.ShouldBe("count >= 10");
            Flow(body, "toBatchSmall").Extensions.ForeignChildren.ShouldBeEmpty();
        }

        WrittenExpression(firstXml, "toBatchLarge").Value.ShouldBe("count >= 10");
        ((string?)WrittenExpression(firstXml, "toBatchLarge").Attribute(Xsi + "type")).ShouldBe("bpmn:tFormalExpression");
    }

    [Fact]
    public void The_expression_language_is_written_back_on_the_definitions_and_a_second_generation_is_stable()
    {
        var (first, firstXml, second, secondXml) = RoundTrip(Document);

        ((string?)XDocument.Parse(firstXml).Root!.Attribute("expressionLanguage")).ShouldBe(Feel);
        second.Definitions.ExpressionLanguage.ShouldBe(first.Definitions.ExpressionLanguage);
        secondXml.ShouldBe(firstXml);
    }

    [Fact]
    public void An_expression_only_flow_reads_without_a_finding()
    {
        // An expression condition is a supported construct: the host evaluates it, and a host that cannot is
        // refused at graph build by the ConditionEvaluation capability, not warned at import (ADR 0011).
        var read = _reader.Read(Document);

        foreach (var flowId in new[] { "toApprove", "toBatch", "toBatchSmall", "toBatchLarge", "toManual" })
            read.Analysis.Issues.ShouldNotContain(issue => issue.ElementId == flowId);
    }

    [Fact]
    public void An_outcome_matched_flow_keeps_its_expression_without_a_finding()
    {
        const string outcomeAndExpression = """
            <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                              xmlns:vw="https://bpmn.valenceworks.io/schema/bpmn" id="Defs">
              <bpmn:process id="P" isExecutable="true">
                <bpmn:startEvent id="Start" />
                <bpmn:endEvent id="Done" />
                <bpmn:sequenceFlow id="F" sourceRef="Start" targetRef="Done" vw:conditionOutcome="Approved">
                  <bpmn:conditionExpression>approved == true</bpmn:conditionExpression>
                </bpmn:sequenceFlow>
              </bpmn:process>
            </bpmn:definitions>
            """;

        var (first, firstXml, _, secondXml) = RoundTrip(outcomeAndExpression);

        var flow = Flow(Process(first), "F");
        flow.ConditionOutcome.ShouldBe("Approved");
        flow.ConditionExpression.ShouldNotBeNull().Body.ShouldBe("approved == true");
        first.Analysis.Issues.ShouldNotContain(issue => issue.ElementId == "F");
        WrittenExpression(firstXml, "F").Value.ShouldBe("approved == true");
        secondXml.ShouldBe(firstXml);
    }

    [Fact]
    public void A_code_built_expression_with_a_language_is_written_as_a_formal_expression()
    {
        var process = TwoStepProcess(new BpmnConditionExpression("x > 1", JavaScript));

        var xml = _writer.Write(process);

        var written = WrittenExpression(xml, "f1");
        written.Value.ShouldBe("x > 1");
        ((string?)written.Attribute("language")).ShouldBe(JavaScript);
        ((string?)written.Attribute(Xsi + "type")).ShouldBe("bpmn:tFormalExpression", "BPMN allows a language only on a formal expression");
        written.Elements().ShouldBeEmpty();

        var reread = _reader.Read(xml);
        Flow(Process(reread), "f1").ConditionExpression.ShouldBe(new BpmnConditionExpression("x > 1", JavaScript),
            "the xsi:type the writer supplied is supplied again on the next write, so it is not retained");
        var (_, _, again, _) = RoundTrip(xml);
        Model(again).ShouldBe(Model(reread));
    }

    [Fact]
    public void A_code_built_expression_without_a_language_is_written_exactly_as_modeled()
    {
        var xml = _writer.Write(TwoStepProcess(new BpmnConditionExpression("x > 1")));

        var written = WrittenExpression(xml, "f1");
        written.Value.ShouldBe("x > 1");
        written.Attributes().ShouldBeEmpty();
        XDocument.Parse(xml).Root!.Attribute("expressionLanguage").ShouldBeNull();
    }

    [Fact]
    public void A_process_written_on_its_own_declares_its_expression_language_on_the_document()
    {
        var process = TwoStepProcess(new BpmnConditionExpression("x > 1")) with { ExpressionLanguage = Feel };

        var xml = _writer.Write(process);

        ((string?)XDocument.Parse(xml).Root!.Attribute("expressionLanguage")).ShouldBe(Feel);
        WrittenExpression(xml, "f1").Attribute("language").ShouldBeNull();
        Process(_reader.Read(xml)).ExpressionLanguage.ShouldBe(Feel);
    }

    [Fact]
    public void An_inherited_language_that_differs_from_the_documents_is_written_onto_the_expression()
    {
        // A process carried into a document that declares another default must not change language on
        // the next read, so its inheriting expressions name their language explicitly.
        var process = TwoStepProcess(new BpmnConditionExpression("x > 1")) with { ExpressionLanguage = JavaScript };
        var definitions = new BpmnDefinitions(Id: "Defs", Processes: [process]) { ExpressionLanguage = Feel };

        var xml = _writer.Write(definitions);

        ((string?)XDocument.Parse(xml).Root!.Attribute("expressionLanguage")).ShouldBe(Feel);
        var written = WrittenExpression(xml, "f1");
        ((string?)written.Attribute("language")).ShouldBe(JavaScript);
        ((string?)written.Attribute(Xsi + "type")).ShouldBe("bpmn:tFormalExpression");

        // BPMN has no per-process expression language, so the read takes the document's default for the
        // process and models the language the writer made explicit as the expression's own. The meaning
        // is unchanged, and from that read on every read, write and read again gives the same model.
        var (first, _, second, _) = RoundTrip(xml);
        Process(first).ExpressionLanguage.ShouldBe(Feel);
        Flow(Process(first), "f1").ConditionExpression.ShouldBe(new BpmnConditionExpression("x > 1", JavaScript));
        Model(second).ShouldBe(Model(first));
    }

    [Theory]
    [InlineData("""<bpmn:conditionExpression language="urn:js">x</bpmn:conditionExpression>""")]
    [InlineData("""<bpmn:conditionExpression xsi:type="bpmn:tFormalExpression" language="urn:js">x</bpmn:conditionExpression>""")]
    [InlineData("""<bpmn:conditionExpression xsi:type="model:tFormalExpression" language="urn:js">x</bpmn:conditionExpression>""")]
    [InlineData("""<bpmn:conditionExpression language="urn:js" id="C" v:origin="imported">x</bpmn:conditionExpression>""")]
    public void A_formal_expression_with_a_language_reads_the_same_model_after_a_write(string expression)
    {
        var (first, firstXml, second, _) = RoundTrip(FlowDocument(expression));

        ((string?)WrittenExpression(firstXml, "F").Attribute(Xsi + "type")).ShouldBe("bpmn:tFormalExpression");
        var modeled = Flow(Process(first), "F").ConditionExpression.ShouldNotBeNull();
        modeled.Language.ShouldBe("urn:js");
        Attribute(modeled, Xsi.NamespaceName, "type").ShouldBeNull("the writer supplies it for any expression with a language");
        Model(second).ShouldBe(Model(first));
    }

    [Theory]
    [InlineData("""<bpmn:conditionExpression xsi:type="v:Script" language="urn:js">x</bpmn:conditionExpression>""", "v:Script")]
    [InlineData("""<bpmn:conditionExpression xsi:type="bpmn:tFormalExpression">x</bpmn:conditionExpression>""", "bpmn:tFormalExpression")]
    [InlineData("""<bpmn:conditionExpression xsi:type="tFormalExpression" language="urn:js">x</bpmn:conditionExpression>""", "tFormalExpression")]
    public void An_xsi_type_the_writer_would_not_reproduce_is_retained(string expression, string type)
    {
        var (first, firstXml, second, _) = RoundTrip(FlowDocument(expression));

        Attribute(Flow(Process(first), "F").ConditionExpression.ShouldNotBeNull(), Xsi.NamespaceName, "type").ShouldBe(type);
        ((string?)WrittenExpression(firstXml, "F").Attribute(Xsi + "type")).ShouldBe(type);
        Model(second).ShouldBe(Model(first));
    }

    [Fact]
    public void A_second_condition_expression_on_one_flow_is_dropped_with_a_finding()
    {
        // BPMN allows at most one conditionExpression per flow. The first is modeled; the rest cannot be
        // represented, so they are reported rather than retained for the writer to drop without a word.
        var (first, firstXml, second, _) = RoundTrip(FlowDocument("""
            <bpmn:conditionExpression>a</bpmn:conditionExpression>
            <bpmn:conditionExpression>b</bpmn:conditionExpression>
            """));

        var flow = Flow(Process(first), "F");
        flow.ConditionExpression.ShouldNotBeNull().Body.ShouldBe("a");
        flow.Extensions.ForeignChildren.ShouldBeEmpty();
        first.Analysis.Issues.ShouldContain(issue =>
            issue.Severity == BpmnImportIssueSeverity.Dropped
            && issue.ElementId == "F"
            && issue.Message.Contains("conditionExpression", StringComparison.Ordinal));

        WrittenExpression(firstXml, "F").Value.ShouldBe("a");
        second.Analysis.Issues.ShouldNotContain(issue => issue.Severity == BpmnImportIssueSeverity.Dropped);
        Model(second).ShouldBe(Model(first));
    }

    [Theory]
    [InlineData("""<bpmn:conditionExpression>outcome == 'Approved'</bpmn:conditionExpression><bpmn:conditionExpression>amount &gt; 5</bpmn:conditionExpression>""")]
    [InlineData("""<bpmn:conditionExpression>amount &gt; 5</bpmn:conditionExpression><bpmn:conditionExpression>outcome == 'Approved'</bpmn:conditionExpression>""")]
    public void A_generated_predicate_beside_an_author_expression_counts_as_a_second_expression(string expressions)
    {
        // The generated predicate is consumed rather than modeled, but the source still declares two
        // conditionExpression elements, which BPMN does not allow; the finding counts both.
        var (first, firstXml, second, _) = RoundTrip(OutcomeFlowDocument("Approved", expressions));

        var flow = Flow(Process(first), "F");
        flow.ConditionOutcome.ShouldBe("Approved");
        flow.ConditionExpression.ShouldNotBeNull().Body.ShouldBe("amount > 5");
        flow.Extensions.ForeignChildren.ShouldBeEmpty();
        first.Analysis.Issues.ShouldContain(issue =>
            issue.Severity == BpmnImportIssueSeverity.Dropped
            && issue.ElementId == "F"
            && issue.Message.Contains("2 conditionExpression elements", StringComparison.Ordinal));

        WrittenExpression(firstXml, "F").Value.ShouldBe("amount > 5");
        second.Analysis.Issues.ShouldNotContain(issue => issue.Severity == BpmnImportIssueSeverity.Dropped);
        Model(second).ShouldBe(Model(first));
    }

    [Fact]
    public void Whitespace_inside_the_text_is_kept_and_whitespace_around_a_cdata_section_is_not()
    {
        // Pins the rule BpmnConditionExpression.Body documents: the reader drops whitespace-only text
        // nodes, so indentation around a CDATA section is not part of the body.
        var text = _reader.Read(FlowDocument("""<bpmn:conditionExpression>  x &gt; 1  </bpmn:conditionExpression>"""));
        Flow(Process(text), "F").ConditionExpression.ShouldNotBeNull().Body.ShouldBe("  x > 1  ");

        var cdata = _reader.Read(FlowDocument("""
            <bpmn:conditionExpression>
              <![CDATA[ y ]]>
            </bpmn:conditionExpression>
            """));
        Flow(Process(cdata), "F").ConditionExpression.ShouldNotBeNull().Body.ShouldBe(" y ");
    }

    [Fact]
    public void A_pre_1_3_payload_with_a_retained_expression_still_writes_it_once()
    {
        // Format 1.2.0 carried an author's expression as a retained foreign child of the flow.
        var retained = new BpmnExtensions(ForeignChildren:
        [
            new BpmnForeignChild(
                new BpmnExtensionElement(
                    new BpmnQName(BpmnXmlNames.Model.NamespaceName, "conditionExpression"),
                    Attributes: [new BpmnForeignAttribute(new BpmnQName(null, "language"), Feel)],
                    Value: "amount > 5"),
                Index: 0)
        ]);
        var legacy = TwoStep(new BpmnSequenceFlow("f1", "start", "done", extensions: retained));

        var legacyXml = _writer.Write(legacy);
        var written = WrittenExpression(legacyXml, "f1");
        written.Value.ShouldBe("amount > 5");
        ((string?)written.Attribute("language")).ShouldBe(Feel);

        var upgradedRead = _reader.Read(legacyXml);
        var upgraded = Flow(Process(upgradedRead), "f1");
        upgraded.ConditionExpression.ShouldNotBeNull().Body.ShouldBe("amount > 5");
        upgraded.Extensions.ForeignChildren.ShouldBeEmpty();
        upgradedRead.Analysis.Issues.ShouldNotContain(issue => issue.Severity == BpmnImportIssueSeverity.Dropped);
        var (_, _, again, _) = RoundTrip(legacyXml);
        Model(again).ShouldBe(Model(upgradedRead));
    }

    [Fact]
    public void A_modeled_expression_wins_over_a_retained_copy_so_only_one_is_written()
    {
        var retained = new BpmnExtensions(ForeignChildren:
        [
            new BpmnForeignChild(
                new BpmnExtensionElement(new BpmnQName(BpmnXmlNames.Model.NamespaceName, "conditionExpression"), Value: "stale"),
                Index: 0)
        ]);
        var both = TwoStep(new BpmnSequenceFlow("f1", "start", "done", extensions: retained)
        {
            ConditionExpression = new BpmnConditionExpression("current")
        });

        WrittenExpression(_writer.Write(both), "f1").Value.ShouldBe("current");
    }

    [Fact]
    public void Semantic_fidelity_keeps_the_body_and_language_and_drops_the_rest()
    {
        var read = _reader.Read(Document, new BpmnImportOptions { Fidelity = BpmnFidelity.Semantic });

        var expression = Flow(Process(read), "toBatch").ConditionExpression.ShouldNotBeNull();
        expression.Body.Trim().ShouldBe("amount >= 1000");
        expression.Language.ShouldBe(JavaScript);
        expression.Extensions.IsEmpty.ShouldBeTrue();
        read.Definitions.ExpressionLanguage.ShouldBe(Feel);
    }

    [Fact]
    public void A_read_document_with_expressions_round_trips_through_json()
    {
        var read = _reader.Read(Document);

        var json = JsonSerializer.Serialize(read.Definitions);
        var restored = JsonSerializer.Deserialize<BpmnDefinitions>(json).ShouldNotBeNull();

        restored.ExpressionLanguage.ShouldBe(Feel);
        restored.Processes.Single().ExpressionLanguage.ShouldBe(Feel);
        Flow(restored.Processes.Single(), "toBatch").ConditionExpression
            .ShouldBe(Flow(Process(read), "toBatch").ConditionExpression);
        _writer.Write(restored, read.Bindings).ShouldBe(_writer.Write(read));
    }

    private static BpmnProcessDefinition TwoStepProcess(BpmnConditionExpression expression) =>
        TwoStep(new BpmnSequenceFlow("f1", "start", "done") { ConditionExpression = expression });

    private static BpmnProcessDefinition TwoStep(BpmnSequenceFlow flow) =>
        new("p", Elements:
        [
            new BpmnElement("start", BpmnElementTypes.StartEvent),
            new BpmnElement("done", BpmnElementTypes.EndEvent)
        ], SequenceFlows: [flow]);
}
