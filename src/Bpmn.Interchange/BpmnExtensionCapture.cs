using System.Xml.Linq;
using Bpmn.Model;

namespace Bpmn.Interchange;

/// <summary>
/// Converts between live XML and the retained <see cref="BpmnExtensions"/> data the model carries, so a
/// read-modify-write cycle keeps documentation, vendor extension elements, foreign attributes, and
/// unrecognized children instead of silently discarding them.
/// </summary>
internal static class BpmnExtensionCapture
{
    /// <summary>
    /// Captures everything on <paramref name="source"/> that the reader itself does not interpret.
    /// <paramref name="isConsumed"/> answers, for one child element, whether the reader read it; every other
    /// child is retained as a foreign child at its position among the source's element children.
    /// <paramref name="vendor"/> decides which names count as the library's own: a vendor name the read
    /// interprets must not also be retained, or the writer emits it twice.
    /// </summary>
    public static BpmnExtensions Capture(
        XElement source,
        BpmnFidelity fidelity,
        BpmnVendorNames vendor,
        Func<XElement, bool> isConsumed,
        RetentionLog log)
    {
        var documentation = new List<BpmnDocumentation>();
        var extensionElements = new List<BpmnExtensionElement>();
        var foreignAttributes = new List<BpmnForeignAttribute>();
        var foreignChildren = new List<BpmnForeignChild>();
        var namespaces = new HashSet<string>(StringComparer.Ordinal);
        var retainedNodes = new Dictionary<string, int>(StringComparer.Ordinal);

        var index = 0;
        foreach (var child in source.Elements())
        {
            var position = index++;

            if (child.Name == BpmnXmlNames.Model + "documentation")
            {
                var textFormat = ((string?)child.Attribute("textFormat"))?.Trim();
                documentation.Add(new BpmnDocumentation(child.Value, string.IsNullOrWhiteSpace(textFormat) ? null : textFormat));
                continue;
            }

            if (child.Name == BpmnXmlNames.Model + "extensionElements")
            {
                foreach (var extension in child.Elements())
                {
                    if (vendor.IsInterpreted(extension.Name)) continue;
                    extensionElements.Add(ToExtensionElement(extension));
                    Tally(extension, namespaces, retainedNodes);
                }

                continue;
            }

            if (isConsumed(child)) continue;

            foreignChildren.Add(new BpmnForeignChild(ToExtensionElement(child), position));
            Tally(child, namespaces, retainedNodes);
        }

        foreach (var attribute in source.Attributes())
        {
            if (attribute.IsNamespaceDeclaration) continue;
            var ns = attribute.Name.Namespace;
            if (ns == XNamespace.None || BpmnXmlNames.IsOwnedNamespace(ns) || ns == BpmnXmlNames.SchemaInstance) continue;
            if (vendor.IsInterpreted(attribute.Name)) continue;

            foreignAttributes.Add(new BpmnForeignAttribute(ToQName(attribute.Name), attribute.Value));
            namespaces.Add(ns.NamespaceName);
            retainedNodes[ns.NamespaceName] = retainedNodes.TryGetValue(ns.NamespaceName, out var count) ? count + 1 : 1;
        }

        foreach (var ns in namespaces)
            log.Record(ns, source.GetPrefixOfNamespace(ns), retainedNodes[ns], fidelity);

        if (fidelity == BpmnFidelity.Semantic)
            return BpmnExtensions.Empty;

        var extensions = new BpmnExtensions(documentation, extensionElements, foreignAttributes, foreignChildren);
        return extensions.IsEmpty ? BpmnExtensions.Empty : extensions;
    }

    /// <summary>
    /// Reads an author's <c>conditionExpression</c> into the model. The body is the element's own text, CDATA
    /// included; <c>language</c> is modeled; everything else is retained on the expression's own
    /// <see cref="BpmnConditionExpression.Extensions"/>. That includes the two things an ordinary capture
    /// skips, the unqualified attributes BPMN gives every base element (<c>id</c>) or every formal expression
    /// (<c>evaluatesToTypeRef</c>), and <c>xsi:type</c>, because whether an expression is formal is part of
    /// what its author wrote.
    /// <para>
    /// One <c>xsi:type</c> is not retained: <c>tFormalExpression</c> under any prefix bound to the BPMN model
    /// namespace, on an expression that declares a <c>language</c>. BPMN allows a language only on a formal
    /// expression, so the writer supplies exactly that type for every expression with a language. Retaining
    /// it as well would make the model depend on whether the source spelled out what the writer adds, and
    /// a read, write and read again would give two different models.
    /// </para>
    /// </summary>
    public static BpmnConditionExpression CaptureConditionExpression(
        XElement source,
        BpmnFidelity fidelity,
        BpmnVendorNames vendor,
        RetentionLog log)
    {
        var body = string.Concat(source.Nodes().OfType<XText>().Select(text => text.Value));
        var language = (string?)source.Attribute("language");
        var extensions = Capture(source, fidelity, vendor, _ => false, log);

        if (fidelity == BpmnFidelity.Semantic)
            return new BpmnConditionExpression(body, language, extensions);

        var hasLanguage = !string.IsNullOrWhiteSpace(language);
        var ownAttributes = source.Attributes()
            .Where(attribute => !attribute.IsNamespaceDeclaration)
            .Where(attribute => attribute.Name == BpmnXmlNames.SchemaInstance + "type"
                ? !(hasLanguage && IsFormalExpressionType(source, attribute.Value))
                : attribute.Name.Namespace == XNamespace.None && attribute.Name.LocalName != "language")
            .Select(attribute => new BpmnForeignAttribute(ToQName(attribute.Name), CaptureAttributeValue(source, attribute)))
            .ToArray();

        if (ownAttributes.Length > 0)
            extensions = extensions with { ForeignAttributes = [.. ownAttributes, .. extensions.ForeignAttributes] };

        return new BpmnConditionExpression(body, language, extensions);
    }

    /// <summary>
    /// Whether an <c>xsi:type</c> value is <c>tFormalExpression</c> under a prefix bound to the BPMN model
    /// namespace in <paramref name="scope"/>: exactly what capture stores as <c>bpmn:tFormalExpression</c>.
    /// An unprefixed value is kept as authored, as <see cref="CaptureAttributeValue"/> keeps it, because
    /// what it resolves to depends on a default namespace the writer does not reproduce.
    /// </summary>
    private static bool IsFormalExpressionType(XElement scope, string value)
    {
        var colon = value.IndexOf(':');
        return colon > 0
            && scope.GetNamespaceOfPrefix(value[..colon]) == BpmnXmlNames.Model
            && StringComparer.Ordinal.Equals(value[(colon + 1)..], "tFormalExpression");
    }

    /// <summary>Rebuilds a retained subtree as live XML.</summary>
    public static XElement ToXml(BpmnExtensionElement element)
    {
        var xml = new XElement(ToXName(element.Name));

        foreach (var attribute in element.Attributes)
            WriteAttribute(xml, attribute);

        foreach (var child in element.Children)
            xml.Add(ToXml(child));

        if (element.Children.Count == 0 && element.Value is { } value)
            xml.Value = value;

        return xml;
    }

    /// <summary>Converts a model-side qualified name to an <see cref="XName"/>.</summary>
    public static XName ToXName(BpmnQName name) =>
        string.IsNullOrEmpty(name.Namespace) ? name.LocalName : XNamespace.Get(name.Namespace!) + name.LocalName;

    private static BpmnQName ToQName(XName name) =>
        new(name.Namespace == XNamespace.None ? null : name.NamespaceName, name.LocalName);

    private static BpmnExtensionElement ToExtensionElement(XElement element)
    {
        var attributes = element.Attributes()
            .Where(attribute => !attribute.IsNamespaceDeclaration)
            .Select(attribute => new BpmnForeignAttribute(ToQName(attribute.Name), CaptureAttributeValue(element, attribute)))
            .ToArray();
        var children = element.Elements().Select(ToExtensionElement).ToArray();
        var value = children.Length == 0 && !string.IsNullOrEmpty(element.Value) ? element.Value : null;

        return new BpmnExtensionElement(ToQName(element.Name), attributes, children, value);
    }

    /// <summary>
    /// Stores <c>xsi:type</c> as an ordinary QName so the payload format stays compatible with 0.3.0.
    /// A prefix that resolves to the BPMN model namespace is normalized to <c>bpmn:&lt;local&gt;</c>;
    /// every other prefix, and any unprefixed value, is kept as authored. Clark names are not used.
    /// </summary>
    private static string CaptureAttributeValue(XElement scope, XAttribute attribute)
    {
        if (attribute.Name != BpmnXmlNames.SchemaInstance + "type")
            return attribute.Value;

        var value = attribute.Value;
        var colon = value.IndexOf(':');
        if (colon <= 0)
            return value;

        var prefix = value[..colon];
        var local = value[(colon + 1)..];
        return scope.GetNamespaceOfPrefix(prefix) == BpmnXmlNames.Model
            ? $"bpmn:{local}"
            : value;
    }

    /// <summary>Writes one retained attribute, restoring the <c>bpmn</c> prefix an <c>xsi:type</c> value needs.</summary>
    public static void WriteAttribute(XElement xml, BpmnForeignAttribute attribute)
    {
        var name = ToXName(attribute.Name);
        if (name == BpmnXmlNames.SchemaInstance + "type")
        {
            WriteTypeAttribute(xml, attribute.Value);
            return;
        }

        xml.SetAttributeValue(name, attribute.Value);
    }

    /// <summary>
    /// Writes <c>xsi:type</c>. Capture already rewrote a prefix bound to the BPMN model namespace as
    /// <c>bpmn:&lt;local&gt;</c>; this method declares <c>xmlns:bpmn</c> so that form resolves. Any
    /// other prefix is written as stored and is not rebound — a leftover <c>model:</c> came from a
    /// prefix that was not bound to BPMN, and must not steal a vendor <c>xmlns:model</c>.
    /// </summary>
    public static void WriteTypeAttribute(XElement xml, string value)
    {
        var colon = value.IndexOf(':');
        if (colon > 0 && value[..colon] == "bpmn")
            xml.SetAttributeValue(XNamespace.Xmlns + "bpmn", BpmnXmlNames.Model.NamespaceName);

        xml.SetAttributeValue(BpmnXmlNames.SchemaInstance + "type", value);
    }

    /// <summary>
    /// Counts retained content per namespace, for the one-finding-per-namespace report. BPMN's own namespaces
    /// are skipped: an unrecognized BPMN element is still retained, but it is not somebody else's extension
    /// and it already has its own finding.
    /// </summary>
    private static void Tally(XElement element, HashSet<string> namespaces, Dictionary<string, int> retainedNodes)
    {
        var ns = element.Name.Namespace;
        if (!BpmnXmlNames.IsOwnedNamespace(ns) && ns != XNamespace.None)
        {
            namespaces.Add(ns.NamespaceName);
            retainedNodes[ns.NamespaceName] = retainedNodes.TryGetValue(ns.NamespaceName, out var count) ? count + 1 : 1;
        }

        foreach (var child in element.Elements())
            Tally(child, namespaces, retainedNodes);
    }
}

/// <summary>
/// Accumulates, per foreign namespace, how much content was retained (or discarded) and across how many
/// elements, so the reader can report one finding per namespace rather than one per node.
/// </summary>
internal sealed class RetentionLog
{
    private readonly Dictionary<string, Tally> _tallies = new(StringComparer.Ordinal);

    public void Record(string ns, string? prefix, int nodes, BpmnFidelity fidelity)
    {
        if (!_tallies.TryGetValue(ns, out var tally))
            tally = _tallies[ns] = new Tally();

        tally.Prefix ??= prefix;
        tally.Nodes += nodes;
        tally.Elements++;
        tally.Discarded |= fidelity == BpmnFidelity.Semantic;
    }

    /// <summary>The per-namespace findings, ordered by namespace so output is deterministic.</summary>
    public IEnumerable<BpmnImportIssue> ToIssues()
    {
        foreach (var (ns, tally) in _tallies.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            var label = tally.Prefix is { Length: > 0 } prefix ? $"'{prefix}'" : $"'{ns}'";
            yield return tally.Discarded
                ? new BpmnImportIssue(
                    BpmnImportIssueSeverity.Degraded,
                    $"Discarded {tally.Nodes} {label} extension node{Plural(tally.Nodes)} across {tally.Elements} element{Plural(tally.Elements)} ({ns}); the read requested semantic fidelity.")
                : new BpmnImportIssue(
                    BpmnImportIssueSeverity.Info,
                    $"Retained {tally.Nodes} {label} extension element{Plural(tally.Nodes)} across {tally.Elements} element{Plural(tally.Elements)} ({ns}).");
        }
    }

    private static string Plural(int count) => count == 1 ? "" : "s";

    private sealed class Tally
    {
        public string? Prefix { get; set; }
        public int Nodes { get; set; }
        public int Elements { get; set; }
        public bool Discarded { get; set; }
    }
}
