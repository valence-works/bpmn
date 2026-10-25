using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Bpmn.Model;
using Shouldly;
using Xunit;

namespace Bpmn.Interchange.Tests;

/// <summary>QName-valued retained XML attributes keep only namespace bindings they had on read.</summary>
public sealed class QNameValueNamespaceTests
{
    private static readonly XNamespace Bpmn = "http://www.omg.org/spec/BPMN/20100524/MODEL";
    private static readonly XNamespace Acme = "urn:example:metadata";
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";
    private const string Schema = "http://www.w3.org/2001/XMLSchema";
    private const string LocalSchema = "urn:example:local-schema";
    private const string VendorModel = "urn:example:vendor-model";

    private const string Document = """
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                          xmlns:acme="urn:example:metadata"
                          xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                          xmlns:xsd="http://www.w3.org/2001/XMLSchema"
                          xmlns:model="http://www.omg.org/spec/BPMN/20100524/MODEL"
                          id="Defs" targetNamespace="urn:example:process"
                          acme:declaredType="xsd:string">
          <bpmn:extensionElements>
            <acme:document acme:declaredType="xsd:string">
              <acme:shadowed xmlns:xsd="urn:example:local-schema" acme:declaredType="xsd:string" />
              <acme:inherited acme:declaredType="xsd:string" />
              <acme:vendorType xmlns:model="urn:example:vendor-model" xsi:type="model:Thing" />
            </acme:document>
          </bpmn:extensionElements>
          <bpmn:process id="P" acme:declaredType="xsd:string">
            <bpmn:startEvent id="Start" />
            <bpmn:exclusiveGateway id="Route" />
            <bpmn:scriptTask id="Task" acme:declaredType="xsd:string" acme:unboundType="ghost:string">
              <bpmn:script acme:declaredType="xsd:string">return 1;</bpmn:script>
              <acme:legacyChild acme:declaredType="xsd:string" />
            </bpmn:scriptTask>
            <bpmn:endEvent id="Done" />
            <bpmn:sequenceFlow id="ToRoute" sourceRef="Start" targetRef="Route" />
            <bpmn:sequenceFlow id="ToTask" sourceRef="Route" targetRef="Task">
              <bpmn:conditionExpression xsi:type="model:tFormalExpression" acme:declaredType="xsd:string">true</bpmn:conditionExpression>
            </bpmn:sequenceFlow>
            <bpmn:sequenceFlow id="ToDone" sourceRef="Task" targetRef="Done" />
          </bpmn:process>
        </bpmn:definitions>
        """;

    private readonly BpmnXmlReader _reader = new();
    private readonly BpmnXmlWriter _writer = new();

    [Fact]
    public void Bound_qname_values_survive_json_transfer_at_each_retention_owner()
    {
        var written = WriteAfterJsonTransfer(Document);
        var root = XDocument.Parse(written).Root!;
        var process = root.Element(Bpmn + "process")!;
        var task = process.Element(Bpmn + "scriptTask")!;
        var condition = process.Elements(Bpmn + "sequenceFlow")
            .Single(flow => (string?)flow.Attribute("id") == "ToTask")
            .Element(Bpmn + "conditionExpression")!;
        var extension = root.Element(Bpmn + "extensionElements")!.Element(Acme + "document")!;

        QNameValue(root, Acme + "declaredType", "xsd:string", Schema);
        QNameValue(process, Acme + "declaredType", "xsd:string", Schema);
        QNameValue(task, Acme + "declaredType", "xsd:string", Schema);
        QNameValue(task.Element(Bpmn + "script")!, Acme + "declaredType", "xsd:string", Schema);
        QNameValue(task.Element(Acme + "legacyChild")!, Acme + "declaredType", "xsd:string", Schema);
        QNameValue(condition, Acme + "declaredType", "xsd:string", Schema);
        QNameValue(extension, Acme + "declaredType", "xsd:string", Schema);
        QNameValue(extension.Element(Acme + "shadowed")!, Acme + "declaredType", "xsd:string", LocalSchema);
        QNameValue(extension.Element(Acme + "inherited")!, Acme + "declaredType", "xsd:string", Schema);

        var normalizedType = condition.Attribute(Xsi + "type").ShouldNotBeNull();
        normalizedType.Value.ShouldBe("bpmn:tFormalExpression");
        condition.GetNamespaceOfPrefix("bpmn").ShouldBe(Bpmn);

        var vendorType = extension.Element(Acme + "vendorType")!;
        vendorType.Attribute(Xsi + "type")!.Value.ShouldBe("model:Thing");
        vendorType.GetNamespaceOfPrefix("model")!.NamespaceName.ShouldBe(VendorModel);
    }

    [Fact]
    public void Unbound_qname_prefix_is_not_invented_by_the_round_trip()
    {
        var written = WriteAfterJsonTransfer(Document);
        var task = XDocument.Parse(written).Descendants(Bpmn + "scriptTask").Single();

        QNameValue(task, Acme + "unboundType", "ghost:string", expectedNamespace: null);
    }

    [Fact]
    public void QName_lists_preserve_each_prefix_binding_and_lexical_spacing()
    {
        var source = XDocument.Parse(Document);
        source.Root!.SetAttributeValue(Acme + "types", " xsd:string model:Thing ");

        var root = XDocument.Parse(WriteAfterJsonTransfer(source.ToString())).Root!;

        root.Attribute(Acme + "types")!.Value.ShouldBe(" xsd:string model:Thing ");
        root.GetNamespaceOfPrefix("xsd").ShouldNotBeNull().NamespaceName.ShouldBe(Schema);
        root.GetNamespaceOfPrefix("model").ShouldBe(Bpmn);
    }

    [Fact]
    public void Generated_namespace_prefix_does_not_bind_an_originally_unbound_value()
    {
        var source = XDocument.Parse(Document);
        source.Root!.SetAttributeValue(Acme + "unboundType", "example:Thing");
        source.Root.Element(Bpmn + "extensionElements")!.Add(new XElement(XName.Get("extra", "https://example.test/vendor")));

        var root = XDocument.Parse(WriteAfterJsonTransfer(source.ToString())).Root!;

        QNameValue(root, Acme + "unboundType", "example:Thing", expectedNamespace: null);
        root.Descendants(XName.Get("extra", "https://example.test/vendor")).ShouldHaveSingleItem();
    }

    [Fact]
    public void A_fixed_prefix_collision_is_refused_instead_of_changing_an_unbound_value()
    {
        var source = XDocument.Parse(Document);
        source.Root!.SetAttributeValue(Acme + "unboundType", "dc:Thing");

        var exception = Should.Throw<BpmnInterchangeException>(() => WriteAfterJsonTransfer(source.ToString()));

        exception.Message.ShouldContain("dc");
    }

    [Fact]
    public void An_originally_unbound_bpmn_type_prefix_is_not_normalized_or_declared()
    {
        var source = XDocument.Parse(Document);
        source.Root!.Attribute(XNamespace.Xmlns + "bpmn")!.Remove();
        source.Root.Element(Bpmn + "extensionElements")!.Add(
            new XElement(Acme + "unbound", new XAttribute(Xsi + "type", "bpmn:Thing")));

        var root = XDocument.Parse(WriteAfterJsonTransfer(source.ToString())).Root!;
        var retained = root.Descendants(Acme + "unbound").Single();

        QNameValue(retained, Xsi + "type", "bpmn:Thing", expectedNamespace: null);
    }

    [Theory]
    [InlineData("https://example.test/type")]
    [InlineData("${xsd:string}")]
    [InlineData("xsd:string + model:Thing")]
    public void Values_outside_the_QName_token_grammar_are_carried_as_opaque_text(string value)
    {
        var source = XDocument.Parse(Document);
        source.Root!.SetAttributeValue(Acme + "opaque", value);

        var root = XDocument.Parse(WriteAfterJsonTransfer(source.ToString())).Root!;

        root.Attribute(Acme + "opaque")!.Value.ShouldBe(value);
    }

    [Fact]
    public void A_URI_like_value_matching_the_QName_grammar_records_its_lexical_prefix()
    {
        var source = XDocument.Parse(Document);
        source.Root!.SetAttributeValue(Acme + "opaque", "urn:value");
        var imported = _reader.Read(source.ToString());
        var attribute = imported.Definitions.Extensions.ForeignAttributes.Single(item => item.Name.LocalName == "opaque");

        attribute.ValueNamespaces.ShouldNotBeNull().ShouldHaveSingleItem().ShouldBe(new BpmnNamespaceBinding("urn", null));
        var written = XDocument.Parse(WriteAfterJsonTransfer(source.ToString())).Root!;
        QNameValue(written, Acme + "opaque", "urn:value", expectedNamespace: null);
    }

    [Fact]
    public void Semantic_only_import_drops_the_foreign_QName_content()
    {
        var result = _reader.Read(Document, new BpmnImportOptions { Fidelity = BpmnFidelity.Semantic });
        var root = XDocument.Parse(_writer.Write(result)).Root!;

        root.DescendantsAndSelf().Attributes().Where(attribute => attribute.Name.Namespace == Acme).ShouldBeEmpty();
        root.Descendants().Where(element => element.Name.Namespace == Acme).ShouldBeEmpty();
    }

    [Fact]
    public void Conflicting_provenance_on_one_element_is_refused_explicitly()
    {
        var imported = _reader.Read(Document);
        var extensions = imported.Definitions.Extensions;
        var conflicting = new BpmnForeignAttribute(new BpmnQName(Acme.NamespaceName, "otherType"), "xsd:Other")
        {
            ValueNamespaces = [new BpmnNamespaceBinding("xsd", LocalSchema)]
        };
        var definitions = imported.Definitions with
        {
            Extensions = extensions with { ForeignAttributes = [.. extensions.ForeignAttributes, conflicting] }
        };

        var exception = Should.Throw<BpmnInterchangeException>(() => _writer.Write(definitions, imported.Bindings));

        exception.Message.ShouldContain("xsd");
        exception.Message.ShouldContain("conflicting namespaces");
    }

    [Fact]
    public void Legacy_manual_xsi_type_without_provenance_keeps_its_existing_writer_behavior()
    {
        var imported = _reader.Read(Document);
        var extensions = imported.Definitions.Extensions;
        var legacy = new BpmnExtensionElement(new BpmnQName(Acme.NamespaceName, "legacy"), Attributes:
        [
            new BpmnForeignAttribute(new BpmnQName(Xsi.NamespaceName, "type"), "bpmn:tFormalExpression")
        ]);
        var definitions = imported.Definitions with
        {
            Extensions = extensions with { ExtensionElements = [.. extensions.ExtensionElements, legacy] }
        };

        var written = XDocument.Parse(_writer.Write(definitions, imported.Bindings));

        QNameValue(written.Descendants(Acme + "legacy").Single(), Xsi + "type", "bpmn:tFormalExpression", Bpmn.NamespaceName);
    }

    [Theory]
    [InlineData("foreign-element")]
    [InlineData("foreign-attribute")]
    [InlineData("owned-element")]
    public void Namespace_shadowing_on_a_foreign_element_survives_real_serialization(string nameUse)
    {
        const string source = """
            <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                              xmlns:foreign="urn:foreign-one" xmlns:v="urn:foreign-one"
                              xmlns:acme="urn:example:metadata" id="Defs"
                              targetNamespace="urn:example:process" acme:rootType="v:Thing">
              <bpmn:extensionElements>
                <foreign:entry xmlns:v="urn:foreign-two" acme:childType="v:Other" acme:unboundType="p1:Thing" />
              </bpmn:extensionElements>
              <bpmn:process id="P">
                <bpmn:startEvent id="Start" />
                <bpmn:endEvent id="End" />
                <bpmn:sequenceFlow id="Flow" sourceRef="Start" targetRef="End" />
              </bpmn:process>
            </bpmn:definitions>
            """;

        var sourceXml = XDocument.Parse(source);
        var sourceEntry = sourceXml.Descendants(XName.Get("entry", "urn:foreign-one")).Single();
        var valuePrefix = "v";
        if (nameUse == "foreign-attribute")
        {
            sourceEntry.Name = Acme + "entry";
            sourceEntry.SetAttributeValue(XName.Get("flag", "urn:foreign-one"), "kept");
        }
        else if (nameUse == "owned-element")
        {
            XNamespace geometry = "http://www.omg.org/spec/DD/20100524/DC";
            sourceXml.Root!.SetAttributeValue(XNamespace.Xmlns + "geo", geometry.NamespaceName);
            sourceEntry.Name = geometry + "Bounds";
            sourceEntry.Attribute(XNamespace.Xmlns + "v")!.Remove();
            sourceEntry.SetAttributeValue(XNamespace.Xmlns + "dc", "urn:foreign-two");
            sourceEntry.SetAttributeValue(Acme + "childType", "dc:Other");
            valuePrefix = "dc";
        }

        var root = XDocument.Parse(WriteAfterJsonTransfer(sourceXml.ToString())).Root!;
        var entry = root.Element(Bpmn + "extensionElements")!.Elements(sourceEntry.Name).ShouldHaveSingleItem();

        QNameValue(root, Acme + "rootType", "v:Thing", "urn:foreign-one");
        QNameValue(entry, Acme + "childType", valuePrefix + ":Other", "urn:foreign-two");
        QNameValue(entry, Acme + "unboundType", "p1:Thing", expectedNamespace: null);
        if (nameUse == "foreign-attribute")
            entry.Attribute(XName.Get("flag", "urn:foreign-one")).ShouldNotBeNull().Value.ShouldBe("kept");
    }

    private string WriteAfterJsonTransfer(string xml)
    {
        var imported = _reader.Read(xml);
        var json = JsonSerializer.Serialize(new DefinitionsEnvelope(imported.Definitions, imported.Bindings));
        var transferred = JsonSerializer.Deserialize<DefinitionsEnvelope>(json)
            ?? throw new InvalidOperationException("The BPMN payload did not deserialize.");

        return _writer.Write(transferred.Definitions, transferred.Bindings);
    }

    private static void QNameValue(XElement element, XName attributeName, string expectedValue, string? expectedNamespace)
    {
        element.Attribute(attributeName).ShouldNotBeNull().Value.ShouldBe(expectedValue);
        var actualNamespace = element.GetNamespaceOfPrefix(expectedValue[..expectedValue.IndexOf(':')]);
        if (expectedNamespace is null)
            actualNamespace.ShouldBeNull();
        else
            actualNamespace.ShouldNotBeNull().NamespaceName.ShouldBe(expectedNamespace);
    }

    private sealed record DefinitionsEnvelope(
        [property: JsonPropertyName("definitions")] BpmnDefinitions Definitions,
        [property: JsonPropertyName("bindings")] IReadOnlyList<BpmnWorkBinding> Bindings);
}
