using System.Text.Json.Serialization;

namespace Bpmn.Model;

/// <summary>
/// A BPMN DI diagram: the visual layout of a process or collaboration.
/// <para>
/// Layout is a first-class part of the model rather than an opaque blob, so a document can be read,
/// modified, and written back without losing the positions a human arranged. Shapes and edges are both
/// preserved; an edge with fewer than two waypoints is not valid BPMN DI and is never emitted.
/// </para>
/// </summary>
/// <param name="Id">The diagram's BPMN DI id.</param>
/// <param name="Name">An optional diagram name.</param>
/// <param name="Plane">The plane holding the shapes and edges.</param>
public sealed record BpmnDiagram(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("plane")] BpmnPlane Plane);

/// <summary>The plane of a <see cref="BpmnDiagram"/>, scoped to one process or collaboration.</summary>
/// <param name="Id">The plane's BPMN DI id.</param>
/// <param name="BpmnElementRef">The process or collaboration this plane lays out.</param>
/// <param name="Shapes">Shapes, one per laid-out flow element, pool, or lane.</param>
/// <param name="Edges">Edges, one per laid-out sequence flow or message flow.</param>
public sealed record BpmnPlane(
    [property: JsonPropertyName("id")] string? Id = null,
    [property: JsonPropertyName("bpmnElementRef")] string? BpmnElementRef = null,
    IReadOnlyList<BpmnShape>? Shapes = null,
    IReadOnlyList<BpmnEdge>? Edges = null)
{
    /// <summary>Shapes. Never null.</summary>
    [JsonPropertyName("shapes")]
    public IReadOnlyList<BpmnShape> Shapes { get; init; } = Shapes ?? [];

    /// <summary>Edges. Never null.</summary>
    [JsonPropertyName("edges")]
    public IReadOnlyList<BpmnEdge> Edges { get; init; } = Edges ?? [];
}

/// <summary>The laid-out box for one flow element, pool, or lane.</summary>
/// <param name="Id">The shape's BPMN DI id.</param>
/// <param name="BpmnElementRef">The element this shape represents.</param>
/// <param name="Bounds">Position and size.</param>
/// <param name="IsHorizontal">Pool and lane orientation; <c>null</c> on ordinary flow elements.</param>
/// <param name="IsExpanded">Whether a subprocess is drawn expanded.</param>
/// <param name="IsMarkerVisible">Whether an exclusive gateway draws its X marker.</param>
/// <param name="Label">Optional label placement.</param>
public sealed record BpmnShape(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("bpmnElementRef")] string BpmnElementRef,
    [property: JsonPropertyName("bounds")] BpmnBounds Bounds,
    [property: JsonPropertyName("isHorizontal")] bool? IsHorizontal = null,
    [property: JsonPropertyName("isExpanded")] bool? IsExpanded = null,
    [property: JsonPropertyName("isMarkerVisible")] bool? IsMarkerVisible = null,
    [property: JsonPropertyName("label")] BpmnLabel? Label = null);

/// <summary>
/// The laid-out route for one sequence flow or message flow. BPMN DI requires at least two waypoints;
/// when a source document carried none, a straight two-point route is synthesized from the endpoint shapes.
/// </summary>
/// <param name="Id">The edge's BPMN DI id.</param>
/// <param name="BpmnElementRef">The flow this edge represents.</param>
/// <param name="Waypoints">The route, in order, from source to target.</param>
/// <param name="Label">Optional label placement.</param>
public sealed record BpmnEdge(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("bpmnElementRef")] string BpmnElementRef,
    IReadOnlyList<BpmnPoint>? Waypoints = null,
    [property: JsonPropertyName("label")] BpmnLabel? Label = null)
{
    /// <summary>The route. Never null.</summary>
    [JsonPropertyName("waypoints")]
    public IReadOnlyList<BpmnPoint> Waypoints { get; init; } = Waypoints ?? [];
}

/// <summary>A point on a diagram plane.</summary>
/// <param name="X">Horizontal coordinate.</param>
/// <param name="Y">Vertical coordinate.</param>
public readonly record struct BpmnPoint(
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y);

/// <summary>An axis-aligned rectangle on a diagram plane.</summary>
/// <param name="X">Left edge.</param>
/// <param name="Y">Top edge.</param>
/// <param name="Width">Width.</param>
/// <param name="Height">Height.</param>
public readonly record struct BpmnBounds(
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y,
    [property: JsonPropertyName("width")] double Width,
    [property: JsonPropertyName("height")] double Height);

/// <summary>Placement of an element's or flow's label, when the author moved it from its default position.</summary>
/// <param name="Bounds">The label box.</param>
public sealed record BpmnLabel(
    [property: JsonPropertyName("bounds")] BpmnBounds? Bounds = null);

/// <summary>Default sizes used when synthesizing layout for a document that carried none.</summary>
public static class BpmnLayoutDefaults
{
    /// <summary>Width and height of an event circle.</summary>
    public const double EventSize = 36d;

    /// <summary>Width and height of a gateway diamond.</summary>
    public const double GatewaySize = 50d;

    /// <summary>Width of a task rectangle.</summary>
    public const double TaskWidth = 100d;

    /// <summary>Height of a task rectangle.</summary>
    public const double TaskHeight = 80d;

    /// <summary>Horizontal distance between successive elements.</summary>
    public const double HorizontalPitch = 180d;
}
