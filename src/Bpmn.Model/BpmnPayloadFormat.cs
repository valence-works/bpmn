namespace Bpmn.Model;

/// <summary>
/// The identity of this library's on-disk payload format.
/// <para>
/// The format is a public contract, versioned independently of the package. A consumer pins to
/// <see cref="Version"/>; the NuGet package version moves for reasons that have nothing to do with the
/// shape of a serialized document, so it is the wrong thing to pin to. See ADR 0005.
/// </para>
/// <para>
/// This version changes only when the serialized shape changes. Adding an optional property with a
/// default that round-trips is a minor bump; renaming, removing, or changing the meaning of a property
/// is a major one. The published JSON Schema carries this version in its <c>$id</c>, so a schema diff
/// and a version bump are the same review.
/// </para>
/// </summary>
public static class BpmnPayloadFormat
{
    /// <summary>
    /// The current payload format version, as a semantic version string.
    /// <para>
    /// Publishing the schema surfaced a real inconsistency: the definition side named its properties in
    /// camelCase through explicit attributes, while the whole of <c>Bpmn.Model.State</c> carried none and
    /// therefore serialized PascalCase, with <c>BpmnProcessDefinition.Extensions</c> a one-off outlier on
    /// the definition side. Fifty-three properties following the wrong one of two conventions in a single
    /// document.
    /// </para>
    /// <para>
    /// Those names were normalized to camelCase before the format was frozen, which is why it began at 1.0.0
    /// rather than something below 1.0. Normalizing cost a day while the library was pre-1.0 with no
    /// published schema behind it; the same change after consumers had generated types from a 1.0 contract
    /// would have been a breaking change in every language at once. Every serialized property now declares
    /// its wire name explicitly, and <c>Model_declares_every_serialized_name</c> keeps it that way.
    /// </para>
    /// <para>
    /// 1.1.0 adds one optional property, <c>BpmnToken.StoppedHandle</c>. A 1.0.0 document reads as before, with
    /// the property absent.
    /// </para>
    /// <para>
    /// 1.2.0 adds the polymorphic <c>BpmnWorkBinding</c> contract from interchange as a schema root,
    /// so hosts can transfer definitions and their bindings together. Existing model shapes are unchanged.
    /// </para>
    /// <para>
    /// 1.3.0 adds three optional properties: <c>BpmnSequenceFlow.ConditionExpression</c> (with the new
    /// <c>BpmnConditionExpression</c> shape) and <c>ExpressionLanguage</c> on <c>BpmnDefinitions</c> and
    /// <c>BpmnProcessDefinition</c>. A 1.2.0 document reads as before, with the properties absent; one that
    /// carried an author's expression as a retained foreign child still writes it back. See ADR 0011.
    /// </para>
    /// <para>
    /// 1.4.0 adds one value to <c>BpmnDiagnosticKind</c>, <c>ConditionEvaluated</c>, appended after every
    /// existing value so none is renumbered. Execution state written by 1.4.0 may carry it; a 1.3.0 document
    /// reads as before. See ADR 0011.
    /// </para>
    /// <para>
    /// 1.5.0 appends two values to <c>BpmnDiagnosticKind</c>, <c>ErrorThrown</c> and <c>ErrorCaught</c>, after
    /// every existing value so none is renumbered. Execution state written by 1.5.0 may carry them; a 1.4.0
    /// document reads as before. See ADR 0012.
    /// </para>
    /// <para>
    /// 1.6.0 adds one optional property, <c>BpmnElement.Script</c>, with the new <c>BpmnScript</c> shape: a
    /// script task's <c>scriptFormat</c> and <c>&lt;script&gt;</c> body. A 1.5.0 document reads as before, with
    /// the property absent; one that carried a script as a retained foreign child still writes it back.
    /// </para>
    /// <para>
    /// 1.7.0 adds one work-binding case, <c>signalPublish</c> (<c>BpmnWorkBinding.SignalPublish</c>): the
    /// broadcast a signal throw or signal end event binds. A 1.6.0 document reads as before; a reader that
    /// predates 1.7.0 rejects the new discriminator rather than dropping the binding. See ADR 0007.
    /// </para>
    /// <para>
    /// 1.8.0 adds one work-binding case, <c>timerDateWait</c> (<c>BpmnWorkBinding.TimerDateWait</c>): the wait a
    /// timer at a <c>&lt;timeDate&gt;</c> binds, carrying the instant as <c>isoDate</c>. An event definition may now
    /// carry the <c>date</c> property, which the model's property bag already allowed. A 1.7.0 document reads as
    /// before; a reader that predates 1.8.0 rejects the new discriminator rather than dropping the binding. See
    /// ADR 0007.
    /// </para>
    /// <para>
    /// 1.9.0 adds one work-binding case, <c>timerCycleWait</c> (<c>BpmnWorkBinding.TimerCycleWait</c>): the wait a
    /// timer on a <c>&lt;timeCycle&gt;</c> binds, carrying the cycle as written and its parts. An event definition may
    /// now carry the <c>cycle</c>, <c>repetitions</c> and <c>start</c> properties, which the model's property bag
    /// already allowed. 1.9.0 also adds one optional property, <c>BpmnToken.FireCount</c> (<c>fireCount</c>): how many
    /// times a scope listener re-armed for a counted timer cycle has fired, which bounds its re-arms. A 1.8.0
    /// document reads as before, with the property absent; a reader that predates 1.9.0 rejects the new discriminator
    /// rather than dropping the binding. See ADR 0007.
    /// </para>
    /// </summary>
    public const string Version = "1.9.0";

    /// <summary>
    /// The <c>$id</c> of the published JSON Schema describing this format.
    /// </summary>
    public const string SchemaId = "https://valence.works/schemas/bpmn/payload/" + Version + ".json";

    /// <summary>
    /// The file name of the published schema, as it appears in the <c>Bpmn.Model</c> package under
    /// <c>schema/</c> and in the repository under <c>src/Bpmn.Model/schema/</c>.
    /// </summary>
    public const string SchemaFileName = "bpmn-payload.schema.json";
}
