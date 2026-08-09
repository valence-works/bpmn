using System.Xml.Linq;

namespace Bpmn.Interchange;

/// <summary>
/// The XML namespaces and element-name tables the reader and writer share.
/// </summary>
public static class BpmnXmlNames
{
    /// <summary>The BPMN 2.0 semantic model namespace.</summary>
    public static readonly XNamespace Model = "http://www.omg.org/spec/BPMN/20100524/MODEL";

    /// <summary>The BPMN DI namespace (<c>BPMNDiagram</c>, <c>BPMNPlane</c>, <c>BPMNShape</c>, <c>BPMNEdge</c>, <c>BPMNLabel</c>).</summary>
    public static readonly XNamespace Di = "http://www.omg.org/spec/BPMN/20100524/DI";

    /// <summary>The Diagram Common namespace (<c>Bounds</c>, <c>Point</c>).</summary>
    public static readonly XNamespace Dc = "http://www.omg.org/spec/DD/20100524/DC";

    /// <summary>The Diagram Interchange namespace (<c>waypoint</c>).</summary>
    public static readonly XNamespace Dd = "http://www.omg.org/spec/DD/20100524/DI";

    /// <summary>
    /// The vendor namespace this library owns, used for the few authoring facts BPMN 2.0 has no standard
    /// representation for: container-scoped variable declarations, multi-instance collection binding,
    /// outcome-matched sequence-flow conditions, and fire-and-forget calls.
    /// <para>
    /// This is the <i>default</i>. A caller that already publishes a vendor namespace of its own sets
    /// <see cref="BpmnImportOptions.VendorNamespace"/> and <see cref="BpmnExportOptions.VendorNamespace"/>
    /// instead, per call.
    /// </para>
    /// </summary>
    public const string VendorNamespaceName = "https://bpmn.valenceworks.io/schema/bpmn";

    /// <summary>The prefix emitted for <see cref="VendorNamespaceName"/>, unless <see cref="BpmnExportOptions.VendorPrefix"/> says otherwise.</summary>
    public const string VendorPrefix = "vw";

    /// <summary>The default vendor namespace as an <see cref="XNamespace"/>.</summary>
    public static readonly XNamespace Vendor = VendorNamespaceName;

    /// <summary>The XML Schema instance namespace, never treated as retained foreign content.</summary>
    public static readonly XNamespace SchemaInstance = "http://www.w3.org/2001/XMLSchema-instance";

    /// <summary>Attribute names this library interprets itself inside the configured vendor namespace, so they are not retained as foreign attributes.</summary>
    public static readonly IReadOnlySet<string> VendorAttributeNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "conditionOutcome",
        "collection",
        "itemVariable",
        "waitForCompletion"
    };

    /// <summary>Element names this library interprets itself inside the configured vendor namespace, so they are not retained as extension elements.</summary>
    public static readonly IReadOnlySet<string> VendorElementNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "variable"
    };

    /// <summary>Task-family local names, mapped to their model element type.</summary>
    public static readonly IReadOnlyDictionary<string, string> TaskLocalNamesToElementTypes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["task"] = "task",
        ["userTask"] = "userTask",
        ["serviceTask"] = "serviceTask",
        ["scriptTask"] = "scriptTask",
        ["manualTask"] = "manualTask",
        ["businessRuleTask"] = "businessRuleTask",
        ["sendTask"] = "sendTask",
        ["receiveTask"] = "receiveTask"
    };

    /// <summary>Gateway local names, mapped to their model element type.</summary>
    public static readonly IReadOnlyDictionary<string, string> GatewayLocalNamesToElementTypes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["exclusiveGateway"] = "exclusiveGateway",
        ["parallelGateway"] = "parallelGateway",
        ["inclusiveGateway"] = "inclusiveGateway",
        ["eventBasedGateway"] = "eventBasedGateway"
    };

    /// <summary>Whether a namespace belongs to this reader, so content in it is interpreted rather than retained.</summary>
    public static bool IsOwnedNamespace(XNamespace ns) =>
        ns == Model || ns == Di || ns == Dc || ns == Dd || ns == XNamespace.Xmlns || ns == XNamespace.Xml;
}
