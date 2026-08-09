using System.Text.Json.Serialization;

namespace Bpmn.Model;

/// <summary>
/// A BPMN pool: a collaboration <c>&lt;participant&gt;</c>. A white-box pool references an imported
/// process through <see cref="ProcessRef"/>; a black-box pool (no <c>processRef</c>) is recorded as a finding
/// only. Visual/organizational: each pool runs as a separately published definition on the name-keyed stimulus
/// fabric, so this record carries no executable semantics the engine reads.
/// </summary>
public sealed class BpmnPool
{
    [JsonConstructor]
    public BpmnPool(string poolId, string? name = null, string? processRef = null, bool isExecutable = true,
        BpmnExtensions? extensions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(poolId);

        PoolId = poolId;
        Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        ProcessRef = string.IsNullOrWhiteSpace(processRef) ? null : processRef.Trim();
        IsExecutable = isExecutable;
        Extensions = extensions ?? BpmnExtensions.Empty;
    }

    [JsonPropertyName("poolId")]
    public string PoolId { get; }

    [JsonPropertyName("name")]
    public string? Name { get; }

    /// <summary>The id of the BPMN process this participant references; <c>null</c> for a black-box pool.</summary>
    [JsonPropertyName("processRef")]
    public string? ProcessRef { get; }

    [JsonPropertyName("isExecutable")]
    public bool IsExecutable { get; }

    /// <summary>Foreign XML retained from this participant. Never null.</summary>
    [JsonPropertyName("extensions")]
    public BpmnExtensions Extensions { get; }
}
