using System.Xml.Linq;
using Bpmn.Model;
using Shouldly;
using Xunit;

namespace Bpmn.Interchange.Tests;

/// <summary>
/// The vendor namespace is configurable per call, on both sides. These tests hold the three promises that
/// makes: the default is unchanged, a configured namespace round-trips, and a name is either interpreted or
/// foreign - never both, which would write it twice, and never neither, which would lose it.
/// </summary>
public sealed class VendorNamespaceTests
{
    private const string OtherNamespace = "https://example.test/schema/process";
    private const string OtherPrefix = "acme";

    private readonly BpmnXmlReader _reader = new();
    private readonly BpmnXmlWriter _writer = new();

    /// <summary>
    /// A model exercising every name the library writes in the vendor namespace: a declared
    /// <c>variable</c>, a <c>collection</c> and <c>itemVariable</c> multi-instance, a
    /// <c>conditionOutcome</c> flow, and a fire-and-forget call's <c>waitForCompletion</c>.
    /// </summary>
    private static readonly BpmnDefinitions Definitions = new(
        Id: "vendor-defs",
        TargetNamespace: "http://example.test/orders",
        Processes:
        [
            new BpmnProcessDefinition("p", Elements:
                [
                    new BpmnElement("start", BpmnElementTypes.StartEvent),
                    new BpmnElement("pick", BpmnElementTypes.UserTask, name: "Pick", bindingRef: "node-pick",
                        loopCharacteristics: new BpmnLoopCharacteristics(collectionVariable: "items", itemVariable: "line")),
                    new BpmnElement("call", BpmnElementTypes.CallActivity, bindingRef: "node-call"),
                    new BpmnElement("done", BpmnElementTypes.EndEvent)
                ],
                SequenceFlows:
                [
                    new BpmnSequenceFlow("f1", "start", "pick", conditionOutcome: "approved"),
                    new BpmnSequenceFlow("f2", "pick", "call"),
                    new BpmnSequenceFlow("f3", "call", "done")
                ],
                Variables: [new BpmnVariableDeclaration("items", "string[]")])
        ]);

    private static readonly BpmnWorkBinding[] Bindings =
    [
        new BpmnWorkBinding.UnboundTask("p", "pick", "node-pick", BpmnBindingSlot.Primary, BpmnElementTypes.UserTask),
        new BpmnWorkBinding.CallProcess("p", "call", "node-call", BpmnBindingSlot.Primary, "shipping", WaitForCompletion: false)
    ];

    private static BpmnImportOptions Reading(string ns, string prefix) => new() { VendorNamespace = ns, VendorPrefix = prefix };

    private static BpmnExportOptions Writing(string ns, string prefix) => new() { VendorNamespace = ns, VendorPrefix = prefix };

    private static BpmnProcessDefinition Process(BpmnImportResult result) => result.Definitions.Processes.Single();

    private static BpmnSequenceFlow Flow(BpmnImportResult result, string flowId) =>
        Process(result).SequenceFlows.Single(flow => flow.FlowId == flowId);

    private static BpmnElement Element(BpmnImportResult result, string elementId) =>
        Process(result).Elements.Single(element => element.ElementId == elementId);

    /// <summary>Every attribute in the document with this local name, whatever its namespace.</summary>
    private static XAttribute[] Attributes(string xml, string localName) =>
        XDocument.Parse(xml).Descendants()
            .SelectMany(element => element.Attributes())
            .Where(attribute => !attribute.IsNamespaceDeclaration && attribute.Name.LocalName == localName)
            .ToArray();

    /// <summary>Every element in the document with this local name, whatever its namespace.</summary>
    private static XElement[] Elements(string xml, string localName) =>
        XDocument.Parse(xml).Descendants().Where(element => element.Name.LocalName == localName).ToArray();

    /// <summary>Asserts that a read resolved all five vendor facts, in whichever namespace it was configured for.</summary>
    private static void ShouldHaveReadEveryVendorFact(BpmnImportResult result)
    {
        Process(result).Variables.ShouldHaveSingleItem().Name.ShouldBe("items");
        Process(result).Variables[0].TypeHint.ShouldBe("string[]");
        Flow(result, "f1").ConditionOutcome.ShouldBe("approved");

        var loop = Element(result, "pick").LoopCharacteristics.ShouldNotBeNull();
        loop.CollectionVariable.ShouldBe("items");
        loop.ItemVariable.ShouldBe("line");

        result.Bindings.OfType<BpmnWorkBinding.CallProcess>().ShouldHaveSingleItem().WaitForCompletion.ShouldBeFalse();
    }

    // -----------------------------------------------------------------------------------------------
    // The default, unchanged
    // -----------------------------------------------------------------------------------------------

    /// <summary>
    /// The regression guard. The options exist to be left alone by everybody who was fine before, so a
    /// default write has to be the same bytes as a write with no options at all.
    /// </summary>
    [Fact]
    public void Defaulted_options_write_exactly_what_no_options_writes()
    {
        var noOptions = _writer.Write(Definitions, Bindings);

        _writer.Write(Definitions, Bindings, new BpmnExportOptions()).ShouldBe(noOptions);
        _writer.Write(Definitions, Bindings, Writing(BpmnXmlNames.VendorNamespaceName, BpmnXmlNames.VendorPrefix)).ShouldBe(noOptions);

        noOptions.ShouldContain($"xmlns:vw=\"{BpmnXmlNames.VendorNamespaceName}\"");
        noOptions.ShouldContain("vw:conditionOutcome=\"approved\"");
        noOptions.ShouldContain("vw:collection=\"items\"");
        noOptions.ShouldContain("vw:itemVariable=\"line\"");
        noOptions.ShouldContain("vw:waitForCompletion=\"false\"");
        noOptions.ShouldContain("<vw:variable name=\"items\"");
    }

    [Fact]
    public void Defaulted_options_read_exactly_what_no_options_reads()
    {
        var xml = _writer.Write(Definitions, Bindings);

        var noOptions = _reader.Read(xml);
        var defaulted = _reader.Read(xml, new BpmnImportOptions());
        var explicitly = _reader.Read(xml, Reading(BpmnXmlNames.VendorNamespaceName, BpmnXmlNames.VendorPrefix));

        ShouldHaveReadEveryVendorFact(noOptions);
        _writer.Write(defaulted).ShouldBe(_writer.Write(noOptions));
        _writer.Write(explicitly).ShouldBe(_writer.Write(noOptions));
    }

    // -----------------------------------------------------------------------------------------------
    // A configured namespace
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public void A_configured_namespace_and_prefix_round_trip_through_write_then_read()
    {
        var xml = _writer.Write(Definitions, Bindings, Writing(OtherNamespace, OtherPrefix));

        xml.ShouldContain($"xmlns:{OtherPrefix}=\"{OtherNamespace}\"");
        xml.ShouldContain($"{OtherPrefix}:conditionOutcome=\"approved\"");
        xml.ShouldContain($"{OtherPrefix}:collection=\"items\"");
        xml.ShouldContain($"{OtherPrefix}:itemVariable=\"line\"");
        xml.ShouldContain($"{OtherPrefix}:waitForCompletion=\"false\"");
        xml.ShouldContain($"<{OtherPrefix}:variable name=\"items\"");
        xml.ShouldNotContain(BpmnXmlNames.VendorNamespaceName);
        xml.ShouldNotContain("vw:");

        var read = _reader.Read(xml, Reading(OtherNamespace, OtherPrefix));

        ShouldHaveReadEveryVendorFact(read);
        read.Analysis.Issues.ShouldNotContain(issue => issue.Severity == BpmnImportIssueSeverity.Dropped);
        // Interpreted content is not also retained, at any of the three levels that could hold it.
        Process(read).Extensions.IsEmpty.ShouldBeTrue();
        Flow(read, "f1").Extensions.IsEmpty.ShouldBeTrue();
        Element(read, "call").Extensions.IsEmpty.ShouldBeTrue();

        // And the second generation is the first, byte for byte.
        _writer.Write(read, Writing(OtherNamespace, OtherPrefix)).ShouldBe(xml);
    }

    [Fact]
    public void The_target_namespace_falls_back_to_the_configured_vendor_namespace()
    {
        var xml = _writer.Write(Definitions with { TargetNamespace = null }, Bindings, Writing(OtherNamespace, OtherPrefix));

        xml.ShouldContain($"targetNamespace=\"{OtherNamespace}\"");
    }

    /// <summary>
    /// The migration path. A model read under one vendor namespace and written under another comes out in
    /// the writer's namespace, wholly - not half-translated, and not in both.
    /// </summary>
    [Fact]
    public void Writing_a_model_read_under_one_namespace_moves_it_to_the_writers()
    {
        var original = _writer.Write(Definitions, Bindings);
        var read = _reader.Read(original);

        var migrated = _writer.Write(read, Writing(OtherNamespace, OtherPrefix));

        migrated.ShouldNotContain(BpmnXmlNames.VendorNamespaceName);
        migrated.ShouldNotContain("vw:");
        ShouldHaveReadEveryVendorFact(_reader.Read(migrated, Reading(OtherNamespace, OtherPrefix)));
    }

    // -----------------------------------------------------------------------------------------------
    // Interpreted versus foreign, across a change of configuration
    // -----------------------------------------------------------------------------------------------

    /// <summary>
    /// The case the whole design turns on. A document authored against one vendor namespace, read by a
    /// consumer configured for another, must not have its annotations quietly claimed as the reader's own -
    /// and must not lose them either. They are somebody else's extension content, which is exactly what
    /// retention is for.
    /// </summary>
    [Fact]
    public void A_document_authored_in_another_vendor_namespace_is_retained_as_foreign_not_interpreted()
    {
        var authored = _writer.Write(Definitions, Bindings);

        var read = _reader.Read(authored, Reading(OtherNamespace, OtherPrefix));

        // Not interpreted: none of it means anything to a read configured for a different namespace.
        Process(read).Variables.ShouldBeEmpty();
        Flow(read, "f1").ConditionOutcome.ShouldBeNull();
        Element(read, "pick").LoopCharacteristics.ShouldBeNull();
        read.Bindings.OfType<BpmnWorkBinding.CallProcess>().ShouldHaveSingleItem().WaitForCompletion.ShouldBeTrue();

        // Not lost either: retained verbatim, in the namespace it was authored in.
        var variable = Process(read).Extensions.ExtensionElements.ShouldHaveSingleItem();
        variable.Name.LocalName.ShouldBe("variable");
        variable.Name.Namespace.ShouldBe(BpmnXmlNames.VendorNamespaceName);
        variable.Attributes.Single(attribute => attribute.Name.LocalName == "name").Value.ShouldBe("items");

        var condition = Flow(read, "f1").Extensions.ForeignAttributes.ShouldHaveSingleItem();
        condition.Name.LocalName.ShouldBe("conditionOutcome");
        condition.Name.Namespace.ShouldBe(BpmnXmlNames.VendorNamespaceName);
        condition.Value.ShouldBe("approved");

        var wait = Element(read, "call").Extensions.ForeignAttributes.ShouldHaveSingleItem();
        wait.Name.LocalName.ShouldBe("waitForCompletion");
        wait.Name.Namespace.ShouldBe(BpmnXmlNames.VendorNamespaceName);
        wait.Value.ShouldBe("false");

        // Writing it back keeps every one of them, once, still in the authoring namespace.
        var written = _writer.Write(read, Writing(OtherNamespace, OtherPrefix));
        Attributes(written, "conditionOutcome").ShouldHaveSingleItem().Name.NamespaceName.ShouldBe(BpmnXmlNames.VendorNamespaceName);
        Attributes(written, "waitForCompletion").ShouldHaveSingleItem().Name.NamespaceName.ShouldBe(BpmnXmlNames.VendorNamespaceName);
        Elements(written, "variable").ShouldHaveSingleItem().Name.NamespaceName.ShouldBe(BpmnXmlNames.VendorNamespaceName);
    }

    /// <summary>
    /// The one vendor name that cannot be retained: <c>collection</c> and <c>itemVariable</c> hang on a
    /// <c>multiInstanceLoopCharacteristics</c> element the reader always consumes, so there is nowhere to
    /// keep them. That is a boundary of the retention promise, and it is reported rather than silent.
    /// </summary>
    [Fact]
    public void A_multi_instance_in_another_vendor_namespace_is_reported_rather_than_reinterpreted()
    {
        var read = _reader.Read(_writer.Write(Definitions, Bindings), Reading(OtherNamespace, OtherPrefix));

        Element(read, "pick").LoopCharacteristics.ShouldBeNull();
        read.Analysis.Issues.ShouldContain(issue =>
            issue.Severity == BpmnImportIssueSeverity.Degraded
            && issue.ElementId == "pick"
            && issue.Message.Contains(BpmnXmlNames.VendorNamespaceName)
            && issue.Message.Contains(OtherNamespace));
    }

    /// <summary>
    /// A document carrying the same annotation in two vendor namespaces, read under one and written under
    /// the other, is where interpreted content and retained content land on the same qualified name. The
    /// model wins and the duplicate is dropped: writing both would produce a document no reader could make
    /// sense of, and XML would not even allow the duplicate attribute.
    /// </summary>
    [Fact]
    public void A_name_carried_in_both_namespaces_is_written_once()
    {
        var document = $"""
            <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                              xmlns:vw="{BpmnXmlNames.VendorNamespaceName}"
                              xmlns:{OtherPrefix}="{OtherNamespace}" id="D">
              <bpmn:process id="p" isExecutable="true">
                <bpmn:extensionElements>
                  <vw:variable name="items" typeHint="string[]" />
                  <{OtherPrefix}:variable name="items" typeHint="number[]" />
                </bpmn:extensionElements>
                <bpmn:startEvent id="start" />
                <bpmn:endEvent id="done" />
                <bpmn:sequenceFlow id="f1" sourceRef="start" targetRef="done"
                                   vw:conditionOutcome="approved" {OtherPrefix}:conditionOutcome="rejected" />
              </bpmn:process>
            </bpmn:definitions>
            """;

        var read = _reader.Read(document);
        Flow(read, "f1").ConditionOutcome.ShouldBe("approved");
        Process(read).Variables.ShouldHaveSingleItem().TypeHint.ShouldBe("string[]");

        var written = _writer.Write(read, Writing(OtherNamespace, OtherPrefix));

        var condition = Attributes(written, "conditionOutcome").ShouldHaveSingleItem();
        condition.Name.NamespaceName.ShouldBe(OtherNamespace);
        condition.Value.ShouldBe("approved");

        var variable = Elements(written, "variable").ShouldHaveSingleItem();
        variable.Name.NamespaceName.ShouldBe(OtherNamespace);
        ((string?)variable.Attribute("typeHint")).ShouldBe("string[]");
    }

    /// <summary>A retained declaration the model does not also carry is kept, rather than filtered out with the duplicates.</summary>
    [Fact]
    public void A_retained_declaration_the_model_does_not_carry_survives_the_same_write()
    {
        var document = $"""
            <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                              xmlns:vw="{BpmnXmlNames.VendorNamespaceName}"
                              xmlns:{OtherPrefix}="{OtherNamespace}" id="D">
              <bpmn:process id="p" isExecutable="true">
                <bpmn:extensionElements>
                  <vw:variable name="items" />
                  <{OtherPrefix}:variable name="total" />
                </bpmn:extensionElements>
                <bpmn:startEvent id="start" />
                <bpmn:endEvent id="done" />
                <bpmn:sequenceFlow id="f1" sourceRef="start" targetRef="done" />
              </bpmn:process>
            </bpmn:definitions>
            """;

        var written = _writer.Write(_reader.Read(document), Writing(OtherNamespace, OtherPrefix));

        Elements(written, "variable")
            .Select(element => (string?)element.Attribute("name"))
            .ShouldBe(["items", "total"], ignoreOrder: true);
    }

    // -----------------------------------------------------------------------------------------------
    // Validation
    // -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void An_empty_vendor_namespace_is_rejected_on_both_sides(string? empty)
    {
        var xml = _writer.Write(Definitions, Bindings);

        Should.Throw<BpmnInterchangeException>(() => _reader.Read(xml, new BpmnImportOptions { VendorNamespace = empty! }))
            .Message.ShouldContain("VendorNamespace");
        Should.Throw<BpmnInterchangeException>(() => _reader.Analyze(xml, new BpmnImportOptions { VendorNamespace = empty! }))
            .Message.ShouldContain("VendorNamespace");
        Should.Throw<BpmnInterchangeException>(() => _writer.Write(Definitions, Bindings, new BpmnExportOptions { VendorNamespace = empty! }))
            .Message.ShouldContain("VendorNamespace");
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("xmlns")]
    [InlineData("xml")]
    [InlineData("not a prefix")]
    [InlineData("1st")]
    public void An_unusable_vendor_prefix_is_rejected(string? prefix)
    {
        Should.Throw<BpmnInterchangeException>(() => _writer.Write(Definitions, Bindings, new BpmnExportOptions { VendorPrefix = prefix! }))
            .Message.ShouldContain("VendorPrefix");
    }
}
