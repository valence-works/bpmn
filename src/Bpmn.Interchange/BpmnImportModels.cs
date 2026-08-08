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
}

/// <summary>
/// The result of a read: the neutral model, the work each element needs, and what the read cost.
/// </summary>
/// <param name="Definitions">The document as a neutral object model.</param>
/// <param name="Bindings">One entry per element that needs host-provided work, keyed back by <see cref="BpmnWorkBinding.BindingRef"/>.</param>
/// <param name="Analysis">The same analysis <see cref="BpmnXmlReader.Analyze"/> would have produced for this document.</param>
/// <param name="ElementExtensions">
/// Foreign XML retained from individual flow elements, sequence flows, and the collaboration. The definitions
/// and process elements carry their own retained content on the model; the per-element types do not, so it
/// rides here. Hand this straight back to <see cref="BpmnXmlWriter"/> to write it out again.
/// </param>
public sealed record BpmnImportResult(
    BpmnDefinitions Definitions,
    IReadOnlyList<BpmnWorkBinding> Bindings,
    BpmnImportAnalysis Analysis,
    IReadOnlyList<BpmnRetainedElement>? ElementExtensions = null)
{
    /// <summary>Retained per-element content. Never null.</summary>
    public IReadOnlyList<BpmnRetainedElement> ElementExtensions { get; init; } = ElementExtensions ?? [];
}

/// <summary>Options for a write.</summary>
public sealed record BpmnExportOptions
{
    /// <summary>The <c>targetNamespace</c> to emit; defaults to the document's own, else the vendor namespace.</summary>
    public string? TargetNamespace { get; init; }

    /// <summary>The <c>exporter</c> attribute to emit; defaults to the document's own.</summary>
    public string? Exporter { get; init; }

    /// <summary>The <c>exporterVersion</c> attribute to emit; defaults to the document's own.</summary>
    public string? ExporterVersion { get; init; }

    /// <summary>Omits the XML declaration from the output.</summary>
    public bool OmitXmlDeclaration { get; init; }
}
