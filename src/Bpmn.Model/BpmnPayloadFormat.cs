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
    /// Those names were normalized to camelCase before the format was frozen, which is why this is 1.0.0
    /// rather than something below 1.0. Normalizing cost a day while the library was pre-1.0 with no
    /// published schema behind it; the same change after consumers had generated types from a 1.0 contract
    /// would have been a breaking change in every language at once. Every serialized property now declares
    /// its wire name explicitly, and <c>Model_declares_every_serialized_name</c> keeps it that way.
    /// </para>
    /// </summary>
    public const string Version = "1.0.0";

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
