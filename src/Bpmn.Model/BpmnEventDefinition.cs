using System.Text.Json.Serialization;

namespace Bpmn.Model;

/// <summary>
/// An event definition attached to a BPMN event element. The engine interprets
/// <see cref="BpmnEventDefinitionTypes.Terminate"/> on end events and
/// <see cref="BpmnEventDefinitionTypes.Timer"/>/<see cref="BpmnEventDefinitionTypes.Message"/>/
/// <see cref="BpmnEventDefinitionTypes.Signal"/> on intermediate catch events (spec 116); other
/// types are carried for later phases.
/// </summary>
public sealed class BpmnEventDefinition
{
    [JsonConstructor]
    public BpmnEventDefinition(string type, IReadOnlyDictionary<string, string>? properties = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);

        Type = type;
        Properties = properties ?? new Dictionary<string, string>();
    }

    [JsonPropertyName("type")]
    public string Type { get; }

    [JsonPropertyName("properties")]
    public IReadOnlyDictionary<string, string> Properties { get; }
}

/// <summary>
/// The <see cref="BpmnEventDefinition.Properties"/> keys this engine slice reads (spec 117). These establish the
/// property-key convention an event-defined start element carries; a later interchange unit populates them from
/// <c>messageRef</c>/<c>signalRef</c>/<c>timerEventDefinition</c>. No key existed before this slice.
/// </summary>
public static class BpmnEventDefinitionProperties
{
    /// <summary>The event name a message/signal start (or catch) resolves its stimulus from (drives <c>EventStimulus.Hash</c>).</summary>
    public const string Name = "name";

    /// <summary>An ISO-8601 duration for a timer start's recurring interval (mutually exclusive with <see cref="Cron"/>).</summary>
    public const string Interval = "interval";

    /// <summary>A cron expression for a timer start's recurring schedule (mutually exclusive with <see cref="Interval"/>).</summary>
    public const string Cron = "cron";

    /// <summary>
    /// The element id a compensate throw/end event targets (spec 124): compensate only that element's
    /// registrations. Absent → compensate everything registered in this process. Set on a
    /// <see cref="BpmnEventDefinitionTypes.Compensation"/> definition of a throw/end event.
    /// </summary>
    public const string ActivityRef = "activityRef";

    /// <summary>
    /// The escalation code an <see cref="BpmnEventDefinitionTypes.Escalation"/> definition carries (spec 127):
    /// the matching key. <b>Required</b> and non-empty on an escalation throw/end event (a throw must say what
    /// it escalates); <b>optional</b> on an escalation boundary event — a code-less boundary is the code-less
    /// catch-all that matches any escalation whose specific code no other boundary on the host claims.
    /// </summary>
    public const string Code = "code";
}

public static class BpmnEventDefinitionTypes
{
    public const string Terminate = "terminate";
    public const string Timer = "timer";
    public const string Message = "message";
    public const string Signal = "signal";
    public const string Error = "error";
    public const string Escalation = "escalation";
    public const string Compensation = "compensation";

    /// <summary>
    /// A cancel event definition (spec 125): on an end event inside a transaction it triggers the transaction
    /// cancellation (compensate the scope's registered work, then complete with the <c>Cancelled</c> outcome);
    /// on a boundary event attached to a transaction host it routes the cancellation path in the parent scope.
    /// </summary>
    public const string Cancel = "cancel";
}
