using System.Text.Json.Serialization;

namespace Bpmn.Model;

/// <summary>A BPMN lane inside a pool. Visual/organizational in Phase 1.</summary>
public sealed class BpmnLane
{
    [JsonConstructor]
    public BpmnLane(string laneId, string? poolId = null, string? name = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(laneId);

        LaneId = laneId;
        PoolId = string.IsNullOrWhiteSpace(poolId) ? null : poolId.Trim();
        Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }

    [JsonPropertyName("laneId")]
    public string LaneId { get; }

    [JsonPropertyName("poolId")]
    public string? PoolId { get; }

    [JsonPropertyName("name")]
    public string? Name { get; }
}
