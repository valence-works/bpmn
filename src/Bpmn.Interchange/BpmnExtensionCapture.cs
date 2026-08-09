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

    /// <summary>Rebuilds a retained subtree as live XML.</summary>
    public static XElement ToXml(BpmnExtensionElement element)
    {
        var xml = new XElement(ToXName(element.Name));

        foreach (var attribute in element.Attributes)
            xml.SetAttributeValue(ToXName(attribute.Name), attribute.Value);

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
            .Select(attribute => new BpmnForeignAttribute(ToQName(attribute.Name), attribute.Value))
            .ToArray();
        var children = element.Elements().Select(ToExtensionElement).ToArray();
        var value = children.Length == 0 && !string.IsNullOrEmpty(element.Value) ? element.Value : null;

        return new BpmnExtensionElement(ToQName(element.Name), attributes, children, value);
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
