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
    /// Deliberately below 1.0. Publishing the schema surfaced a real inconsistency in the format: the
    /// definition side names its properties in camelCase through explicit attributes, while the whole of
    /// <c>Bpmn.Model.State</c> carries no attributes and therefore serializes PascalCase, and
    /// <c>BpmnProcessDefinition.Extensions</c> is a one-off outlier on the definition side. That is 53
    /// properties following the wrong one of two conventions in a single document.
    /// </para>
    /// <para>
    /// The schema describes the format as it actually is, because a schema that flatters the model is
    /// worse than none. But calling that 1.0.0 would freeze the inconsistency into a contract on the day
    /// it was discovered. 0.1.0 says what is true: described, versioned, and not yet frozen.
    /// </para>
    /// </summary>
    public const string Version = "0.1.0";

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
