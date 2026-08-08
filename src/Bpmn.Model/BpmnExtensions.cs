using System.Text.Json.Serialization;

namespace Bpmn.Model;

/// <summary>
/// Foreign and non-semantic XML retained verbatim from a BPMN document, so a read-modify-write cycle does
/// not silently discard it. Attached to the definitions element, each process, and each flow element.
/// <para>
/// This is what makes vendor annotations survive: <c>camunda:*</c>, <c>zeebe:*</c>, <c>flowable:*</c> and
/// anything else the reader does not itself interpret is kept here and written back out.
/// </para>
/// <para>
/// Retention is <b>lossless for content, lossy for formatting</b>. Attribute order, whitespace, comments,
/// CDATA-versus-text, and namespace prefix choices are not preserved; element structure, names, values, and
/// document order are. That is a deliberate trade: a semantic round-trip a diff tool can compare is more
/// useful than a byte-exact one, and byte-exactness is unreachable without a source-preserving parser.
/// </para>
/// </summary>
/// <param name="Documentation">The element's <c>&lt;documentation&gt;</c> entries, in document order.</param>
/// <param name="ExtensionElements">Children of <c>&lt;extensionElements&gt;</c>, in document order.</param>
/// <param name="ForeignAttributes">Attributes from a namespace the reader does not own.</param>
/// <param name="ForeignChildren">Unrecognized child elements, with the index needed to restore their position.</param>
public sealed record BpmnExtensions(
    IReadOnlyList<BpmnDocumentation>? Documentation = null,
    IReadOnlyList<BpmnExtensionElement>? ExtensionElements = null,
    IReadOnlyList<BpmnForeignAttribute>? ForeignAttributes = null,
    IReadOnlyList<BpmnForeignChild>? ForeignChildren = null)
{
    /// <summary>An empty, shared instance.</summary>
    public static BpmnExtensions Empty { get; } = new();

    /// <summary>Documentation entries. Never null.</summary>
    [JsonPropertyName("documentation")]
    public IReadOnlyList<BpmnDocumentation> Documentation { get; init; } = Documentation ?? [];

    /// <summary>Extension elements. Never null.</summary>
    [JsonPropertyName("extensionElements")]
    public IReadOnlyList<BpmnExtensionElement> ExtensionElements { get; init; } = ExtensionElements ?? [];

    /// <summary>Foreign attributes. Never null.</summary>
    [JsonPropertyName("foreignAttributes")]
    public IReadOnlyList<BpmnForeignAttribute> ForeignAttributes { get; init; } = ForeignAttributes ?? [];

    /// <summary>Foreign child elements. Never null.</summary>
    [JsonPropertyName("foreignChildren")]
    public IReadOnlyList<BpmnForeignChild> ForeignChildren { get; init; } = ForeignChildren ?? [];

    /// <summary>
    /// Retained content compares by value, including its collections.
    /// <para>
    /// A record does not give this for free: compiler-generated equality compares collection members by
    /// reference, so two structurally identical extension trees would otherwise be unequal. Diffing two
    /// models is a first-class use case here, so the comparison is written out.
    /// </para>
    /// </summary>
    public bool Equals(BpmnExtensions? other) =>
    other is not null
    && Documentation.SequenceEqual(other.Documentation)
    && ExtensionElements.SequenceEqual(other.ExtensionElements)
    && ForeignAttributes.SequenceEqual(other.ForeignAttributes)
    && ForeignChildren.SequenceEqual(other.ForeignChildren);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var item in Documentation)
            hash.Add(item);
        foreach (var item in ExtensionElements)
            hash.Add(item);
        foreach (var item in ForeignAttributes)
            hash.Add(item);
        foreach (var item in ForeignChildren)
            hash.Add(item);
        return hash.ToHashCode();
    }

    /// <summary>Whether nothing at all was retained.</summary>
    [JsonIgnore]
    public bool IsEmpty =>
    Documentation.Count == 0
    && ExtensionElements.Count == 0
    && ForeignAttributes.Count == 0
    && ForeignChildren.Count == 0;

    /// <summary>Every distinct namespace appearing in the retained content, for reporting.</summary>
    public IReadOnlyCollection<string> RetainedNamespaces()
    {
        var namespaces = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var attribute in ForeignAttributes)
            if (!string.IsNullOrEmpty(attribute.Name.Namespace))
                namespaces.Add(attribute.Name.Namespace!);

        foreach (var element in ExtensionElements)
            Collect(element, namespaces);
        foreach (var child in ForeignChildren)
            Collect(child.Element, namespaces);

        return namespaces;
    }

    private static void Collect(BpmnExtensionElement element, SortedSet<string> namespaces)
    {
        if (!string.IsNullOrEmpty(element.Name.Namespace))
            namespaces.Add(element.Name.Namespace!);
        foreach (var attribute in element.Attributes)
            if (!string.IsNullOrEmpty(attribute.Name.Namespace))
                namespaces.Add(attribute.Name.Namespace!);
        foreach (var child in element.Children)
            Collect(child, namespaces);
    }
}

/// <summary>A namespace-qualified XML name.</summary>
/// <param name="Namespace">The namespace URI, or <c>null</c> for an unqualified name.</param>
/// <param name="LocalName">The local name.</param>
public readonly record struct BpmnQName(
    [property: JsonPropertyName("ns")] string? Namespace,
    [property: JsonPropertyName("localName")] string LocalName)
{
    /// <summary>Renders as <c>{namespace}localName</c>, matching the XName convention.</summary>
    public override string ToString() => string.IsNullOrEmpty(Namespace) ? LocalName : $"{{{Namespace}}}{LocalName}";
}

/// <summary>One retained attribute.</summary>
/// <param name="Name">The qualified attribute name.</param>
/// <param name="Value">The attribute value.</param>
public sealed record BpmnForeignAttribute(
    [property: JsonPropertyName("name")] BpmnQName Name,
    [property: JsonPropertyName("value")] string Value);

/// <summary>A <c>&lt;documentation&gt;</c> entry.</summary>
/// <param name="Text">The documentation text.</param>
/// <param name="TextFormat">The declared <c>textFormat</c>, when present.</param>
public sealed record BpmnDocumentation(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("textFormat")] string? TextFormat = null);

/// <summary>
/// One retained XML subtree, held as data rather than as a live XML node so it survives JSON
/// serialization, compares by value, and cannot be mutated behind the model's back.
/// </summary>
/// <param name="Name">The qualified element name.</param>
/// <param name="Attributes">Attributes on this element.</param>
/// <param name="Children">Child elements, in document order.</param>
/// <param name="Value">Text content, when the element has no element children.</param>
public sealed record BpmnExtensionElement(
    [property: JsonPropertyName("name")] BpmnQName Name,
    IReadOnlyList<BpmnForeignAttribute>? Attributes = null,
    IReadOnlyList<BpmnExtensionElement>? Children = null,
    [property: JsonPropertyName("value")] string? Value = null)
{
    /// <summary>Attributes. Never null.</summary>
    [JsonPropertyName("attributes")]
    public IReadOnlyList<BpmnForeignAttribute> Attributes { get; init; } = Attributes ?? [];

    /// <summary>Child elements. Never null.</summary>
    [JsonPropertyName("children")]
    public IReadOnlyList<BpmnExtensionElement> Children { get; init; } = Children ?? [];

    /// <summary>Compares the whole subtree by value, recursing through <see cref="Children"/>.</summary>
    public bool Equals(BpmnExtensionElement? other) =>
    other is not null
    && Name.Equals(other.Name)
    && string.Equals(Value, other.Value, StringComparison.Ordinal)
    && Attributes.SequenceEqual(other.Attributes)
    && Children.SequenceEqual(other.Children);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Name);
        hash.Add(Value);
        foreach (var attribute in Attributes)
            hash.Add(attribute);
        foreach (var child in Children)
            hash.Add(child);
        return hash.ToHashCode();
    }
}

/// <summary>
/// An unrecognized child element, together with the position it occupied.
/// <para>
/// The index matters: BPMN's <c>tFlowNode</c> uses an <c>xsd:sequence</c>, so re-emitting retained children
/// in arbitrary positions produces schema-invalid XML that modeling tools reject. The writer restores each
/// retained child at its recorded index within the canonical child order for its element type.
/// </para>
/// </summary>
/// <param name="Element">The retained subtree.</param>
/// <param name="Index">The zero-based position this child occupied among its siblings.</param>
public sealed record BpmnForeignChild(
    [property: JsonPropertyName("element")] BpmnExtensionElement Element,
    [property: JsonPropertyName("index")] int Index);

/// <summary>How much of a source document the reader retains.</summary>
public enum BpmnFidelity
{
    /// <summary>Retain only what the semantics need. Foreign content is reported and dropped.</summary>
    Semantic,

    /// <summary>
    /// Retain documentation, extension elements, foreign attributes, and unknown children so they survive a
    /// read-modify-write cycle. The default.
    /// </summary>
    Lossless
}
