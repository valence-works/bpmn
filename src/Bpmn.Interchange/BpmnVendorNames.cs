using System.Xml;
using System.Xml.Linq;

namespace Bpmn.Interchange;

/// <summary>
/// The vendor namespace one read or one write resolved, together with the qualified names the library
/// interprets inside it.
/// <para>
/// BPMN 2.0 has no standard representation for a few authoring facts - container-scoped variable
/// declarations, multi-instance collection binding, outcome-matched sequence-flow conditions, and a
/// fire-and-forget call - so this library writes them in a vendor namespace of its own. Which namespace that
/// is, is a per-call decision: a host that already ships its own vendor namespace configures it through
/// <see cref="BpmnImportOptions.VendorNamespace"/> and <see cref="BpmnExportOptions.VendorNamespace"/>
/// rather than translating the document on both sides of every call.
/// </para>
/// <para>
/// The resolved namespace is what decides whether a name is <i>interpreted</i> - read into the model and
/// written back from it - or <i>foreign</i>, retained verbatim in <see cref="Bpmn.Model.BpmnExtensions"/>.
/// That is why the decision travels as one object: the reader, the writer, and the extension capture all
/// have to answer the question the same way, or the same attribute is both interpreted and retained and
/// gets written twice.
/// </para>
/// </summary>
internal sealed class BpmnVendorNames
{
    /// <summary>The names built from <see cref="BpmnXmlNames.VendorNamespaceName"/> and <see cref="BpmnXmlNames.VendorPrefix"/>.</summary>
    public static readonly BpmnVendorNames Default = new(BpmnXmlNames.VendorNamespaceName, BpmnXmlNames.VendorPrefix);

    private BpmnVendorNames(string namespaceName, string prefix)
    {
        NamespaceName = namespaceName;
        Prefix = prefix;
        Namespace = XNamespace.Get(namespaceName);
        ConditionOutcome = Namespace + "conditionOutcome";
        Collection = Namespace + "collection";
        ItemVariable = Namespace + "itemVariable";
        WaitForCompletion = Namespace + "waitForCompletion";
        Variable = Namespace + "variable";
    }

    /// <summary>The resolved vendor namespace.</summary>
    public XNamespace Namespace { get; }

    /// <summary>The resolved vendor namespace as a string, for the <c>xmlns</c> declaration and the target-namespace fallback.</summary>
    public string NamespaceName { get; }

    /// <summary>The prefix the writer declares <see cref="Namespace"/> under.</summary>
    public string Prefix { get; }

    /// <summary>The sequence-flow attribute carrying an outcome-matched condition.</summary>
    public XName ConditionOutcome { get; }

    /// <summary>The multi-instance attribute naming the collection variable to loop over.</summary>
    public XName Collection { get; }

    /// <summary>The multi-instance attribute naming the per-iteration item variable.</summary>
    public XName ItemVariable { get; }

    /// <summary>The call-activity attribute saying the call is fire-and-forget.</summary>
    public XName WaitForCompletion { get; }

    /// <summary>The extension element declaring a container-scoped variable.</summary>
    public XName Variable { get; }

    /// <summary>
    /// Whether a name is one this library reads into the model and writes back out of it, rather than
    /// foreign content to retain verbatim. A name in some <i>other</i> vendor namespace is foreign even when
    /// its local name matches, which is what lets a document authored against one vendor namespace be read
    /// under another without its annotations being silently reinterpreted or lost.
    /// </summary>
    public bool IsInterpreted(XName name) =>
        name.Namespace == Namespace
        && (BpmnXmlNames.VendorAttributeNames.Contains(name.LocalName) || BpmnXmlNames.VendorElementNames.Contains(name.LocalName));

    /// <summary>Resolves the vendor names for one read.</summary>
    public static BpmnVendorNames For(BpmnImportOptions? options) =>
        options is null ? Default : Resolve(options.VendorNamespace, options.VendorPrefix, nameof(BpmnImportOptions));

    /// <summary>Resolves the vendor names for one write.</summary>
    public static BpmnVendorNames For(BpmnExportOptions? options) =>
        options is null ? Default : Resolve(options.VendorNamespace, options.VendorPrefix, nameof(BpmnExportOptions));

    private static BpmnVendorNames Resolve(string? namespaceName, string? prefix, string owner)
    {
        if (string.IsNullOrWhiteSpace(namespaceName))
            throw new BpmnInterchangeException(
                $"{owner}.VendorNamespace must be a non-empty XML namespace; leave it unset to use the default '{BpmnXmlNames.VendorNamespaceName}'.");

        var validPrefix = ValidatePrefix(prefix, owner);

        return StringComparer.Ordinal.Equals(namespaceName, BpmnXmlNames.VendorNamespaceName)
               && StringComparer.Ordinal.Equals(validPrefix, BpmnXmlNames.VendorPrefix)
            ? Default
            : new BpmnVendorNames(namespaceName, validPrefix);
    }

    /// <summary>
    /// A prefix has to survive being declared as <c>xmlns:prefix</c>. Checking it here turns a malformed
    /// prefix into a sentence naming the option, instead of an <see cref="XmlException"/> raised from the
    /// middle of a write with no indication of which option caused it.
    /// </summary>
    private static string ValidatePrefix(string? prefix, string owner)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            throw new BpmnInterchangeException(
                $"{owner}.VendorPrefix must be a non-empty XML namespace prefix; leave it unset to use the default '{BpmnXmlNames.VendorPrefix}'.");

        if (prefix is "xml" or "xmlns")
            throw new BpmnInterchangeException($"{owner}.VendorPrefix cannot be '{prefix}', which XML reserves.");

        try
        {
            XmlConvert.VerifyNCName(prefix);
        }
        catch (XmlException exception)
        {
            throw new BpmnInterchangeException($"{owner}.VendorPrefix '{prefix}' is not a valid XML namespace prefix.", exception);
        }

        return prefix;
    }
}
