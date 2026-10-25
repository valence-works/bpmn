namespace Bpmn.Interchange;

/// <summary>What a host knows about its ability to execute one imported work binding.</summary>
public enum BpmnWorkAvailability
{
    /// <summary>The host has not established whether it can execute this binding.</summary>
    Unknown,

    /// <summary>The host has an implementation for this binding.</summary>
    Available,

    /// <summary>The host knows it cannot execute this binding.</summary>
    Unavailable
}

/// <summary>The host-availability assessment for one work binding returned by an import.</summary>
/// <param name="Binding">The original binding descriptor, including its process, element, ref, and slot.</param>
/// <param name="Availability">The host's classification for this binding.</param>
public sealed record BpmnWorkAvailabilityResult(BpmnWorkBinding Binding, BpmnWorkAvailability Availability)
{
    /// <summary>
    /// A generic diagnostic for work the host reported as unavailable; <c>null</c> for available or unknown work.
    /// The binding carries structured identity, while the host classifier owns the availability decision.
    /// </summary>
    public string? Diagnostic => Availability == BpmnWorkAvailability.Unavailable
        ? $"Host work is unavailable for {Binding.Slot} binding '{Binding.BindingRef}' on element '{Binding.ElementId}' in process '{Binding.ProcessId}'."
        : null;
}

/// <summary>The ordered host-availability assessment of every supplied binding.</summary>
public sealed record BpmnWorkAvailabilityAssessment
{
    /// <summary>Creates an assessment over a defensive, read-only snapshot of the results.</summary>
    public BpmnWorkAvailabilityAssessment(IEnumerable<BpmnWorkAvailabilityResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        var snapshot = results.ToArray();
        if (snapshot.Any(result => result is null))
            throw new ArgumentException("An availability assessment cannot contain a null result.", nameof(results));

        Results = Array.AsReadOnly(snapshot);
    }

    /// <summary>The result for every binding, in the order supplied to <see cref="BpmnWorkAvailabilityValidator.Assess"/>.</summary>
    public IReadOnlyList<BpmnWorkAvailabilityResult> Results { get; }

    /// <summary>True only when every supplied binding is known to be available; an empty assessment is ready.</summary>
    public bool AllWorkAvailable => Results.All(result => result.Availability == BpmnWorkAvailability.Available);
}

/// <summary>Assesses host availability without changing the imported model or its work bindings.</summary>
public static class BpmnWorkAvailabilityValidator
{
    /// <summary>
    /// Classifies every supplied binding in order. Unknown results remain visible and do not count as ready;
    /// unavailable results carry a generic diagnostic naming the binding's structured identity.
    /// </summary>
    /// <param name="bindings">The binding descriptors to assess, typically <see cref="BpmnImportResult.Bindings"/>.</param>
    /// <param name="classifier">The host's availability decision for each binding. Called exactly once per descriptor.</param>
    /// <returns>The ordered assessment. An empty binding set is ready because it requires no host work.</returns>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    /// <exception cref="ArgumentException">The sequence contains a null binding.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The classifier returns an undefined availability value.</exception>
    /// <remarks>Classifier exceptions propagate to the caller.</remarks>
    public static BpmnWorkAvailabilityAssessment Assess(
        IEnumerable<BpmnWorkBinding> bindings,
        Func<BpmnWorkBinding, BpmnWorkAvailability> classifier)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(classifier);

        var results = new List<BpmnWorkAvailabilityResult>();
        var index = 0;
        foreach (var binding in bindings)
        {
            if (binding is null)
                throw new ArgumentException($"Binding at index {index} is null.", nameof(bindings));

            var availability = classifier(binding);
            if (!Enum.IsDefined(availability))
                throw new ArgumentOutOfRangeException(nameof(classifier), availability, "The classifier returned an undefined work-availability value.");

            results.Add(new BpmnWorkAvailabilityResult(binding, availability));
            index++;
        }

        return new BpmnWorkAvailabilityAssessment(results);
    }
}
