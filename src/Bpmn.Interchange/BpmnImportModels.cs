using Bpmn.Model;

namespace Bpmn.Interchange;

/// <summary>
/// What a document contains and how much of it survives a read: the processes it declares, a histogram of
/// the BPMN elements encountered, and every element-scoped finding. Produced by both
/// <see cref="BpmnXmlReader.Analyze"/> and <see cref="BpmnXmlReader.Read"/>, from the same code path, so a
/// dry run can never disagree with the real one.
/// </summary>
/// <param name="ProcessIds">The <c>id</c> of every <c>&lt;process&gt;</c> in the document, in document order.</param>
/// <param name="ElementCounts">How many of each BPMN local name were encountered, across all containers.</param>
/// <param name="Issues">Every finding, in the order the reader produced it.</param>
public sealed record BpmnImportAnalysis(
    IReadOnlyCollection<string> ProcessIds,
    IReadOnlyDictionary<string, int> ElementCounts,
    IReadOnlyCollection<BpmnImportIssue> Issues);

/// <summary>One element-scoped finding from a read.</summary>
/// <param name="Severity">How much was lost.</param>
/// <param name="Message">A sentence naming the element, what the document said, and what the reader did about it.</param>
/// <param name="ElementId">The BPMN id the finding is about, when it is about one element.</param>
/// <param name="ProcessId">The process the finding occurred in, when it is scoped to one.</param>
public sealed record BpmnImportIssue(
    BpmnImportIssueSeverity Severity,
    string Message,
    string? ElementId = null,
    string? ProcessId = null);

/// <summary>How much of an element's authored meaning survived the read.</summary>
public enum BpmnImportIssueSeverity
{
    /// <summary>The element read cleanly; the finding is a note, such as a task that binds no implementation.</summary>
    Info,

    /// <summary>The element read in a reduced form, such as an expression condition that became an unconditional flow.</summary>
    Degraded,

    /// <summary>The element could not be represented and was dropped, along with the flows that referenced it.</summary>
    Dropped
}

/// <summary>Options for a read.</summary>
public sealed record BpmnImportOptions
{
    /// <summary>
    /// The process the caller cares about. Every process in the document is still read; naming one that the
    /// document does not declare fails fast rather than silently reading something else.
    /// </summary>
    public string? ProcessId { get; init; }

    /// <summary>The prefix for generated binding refs. Defaults to <c>node</c>, giving <c>node-{elementId}</c>.</summary>
    public string? BindingRefPrefix { get; init; }

    /// <summary>
    /// How much of the source document to retain. Defaults to <see cref="BpmnFidelity.Lossless"/>, so
    /// documentation, extension elements, foreign attributes, and unrecognized children survive a
    /// read-modify-write cycle.
    /// </summary>
    public BpmnFidelity Fidelity { get; init; } = BpmnFidelity.Lossless;

    /// <summary>
    /// The XML namespace whose attributes and elements this read interprets as the library's own, rather
    /// than retaining them as foreign content. Defaults to
    /// <see cref="BpmnXmlNames.VendorNamespaceName"/>; a host that already ships a vendor namespace of its
    /// own names it here.
    /// <para>
    /// This is the namespace of <c>conditionOutcome</c>, <c>collection</c>, <c>itemVariable</c>,
    /// <c>waitForCompletion</c>, and <c>&lt;variable&gt;</c>. Those names in any <i>other</i> namespace are
    /// foreign: they are retained verbatim and are not read into the model.
    /// </para>
    /// </summary>
    public string VendorNamespace { get; init; } = BpmnXmlNames.VendorNamespaceName;

    /// <summary>
    /// The prefix the vendor namespace is reported under in retention findings. Defaults to
    /// <see cref="BpmnXmlNames.VendorPrefix"/>. A read resolves names by namespace, never by prefix, so this
    /// changes wording rather than meaning.
    /// </summary>
    public string VendorPrefix { get; init; } = BpmnXmlNames.VendorPrefix;
}

/// <summary>
/// The result of a read: the neutral model, the work each element needs, and what the read cost.
/// </summary>
/// <param name="Definitions">The document as a neutral object model.</param>
/// <param name="Bindings">One entry per element that needs host-provided work, keyed back by <see cref="BpmnWorkBinding.BindingRef"/>.</param>
/// <param name="Analysis">The same analysis <see cref="BpmnXmlReader.Analyze"/> would have produced for this document.</param>
public sealed record BpmnImportResult(
    BpmnDefinitions Definitions,
    IReadOnlyList<BpmnWorkBinding> Bindings,
    BpmnImportAnalysis Analysis);

/// <summary>Options for a write.</summary>
public sealed record BpmnExportOptions
{
    /// <summary>The <c>targetNamespace</c> to emit; defaults to the document's own, else <see cref="VendorNamespace"/>.</summary>
    public string? TargetNamespace { get; init; }

    /// <summary>
    /// The XML namespace to write the library's own non-standard attributes and elements in. Defaults to
    /// <see cref="BpmnXmlNames.VendorNamespaceName"/>; a host that already ships a vendor namespace of its
    /// own names it here.
    /// <para>
    /// The writer always emits <c>conditionOutcome</c>, <c>collection</c>, <c>itemVariable</c>,
    /// <c>waitForCompletion</c>, and <c>&lt;variable&gt;</c> in this namespace, whichever namespace the model
    /// was read from. Reading with one and writing with another is therefore a migration: the document comes
    /// out in the writer's namespace.
    /// </para>
    /// </summary>
    public string VendorNamespace { get; init; } = BpmnXmlNames.VendorNamespaceName;

    /// <summary>
    /// The prefix <see cref="VendorNamespace"/> is declared under on the root element. Defaults to
    /// <see cref="BpmnXmlNames.VendorPrefix"/>.
    /// </summary>
    public string VendorPrefix { get; init; } = BpmnXmlNames.VendorPrefix;

    /// <summary>The <c>exporter</c> attribute to emit; defaults to the document's own.</summary>
    public string? Exporter { get; init; }

    /// <summary>The <c>exporterVersion</c> attribute to emit; defaults to the document's own.</summary>
    public string? ExporterVersion { get; init; }

    /// <summary>Omits the XML declaration from the output.</summary>
    public bool OmitXmlDeclaration { get; init; }
}
