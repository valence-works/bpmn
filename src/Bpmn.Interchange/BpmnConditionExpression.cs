using System.Xml.Linq;
using Bpmn.Model;

namespace Bpmn.Interchange;

/// <summary>
/// Distinguishes an author's <c>conditionExpression</c> from the predicate this library writes for a
/// <c>conditionOutcome</c>. The generated form is exactly <c>outcome == 'X'</c> with no attributes and
/// no child elements — the only shape <see cref="BpmnXmlWriter"/> emits on its own.
/// </summary>
internal static class BpmnConditionExpression
{
    public const string ElementLocalName = "conditionExpression";

    /// <summary>The body the writer emits for an outcome-matched flow that carried no author expression.</summary>
    public static string GeneratedBody(string conditionOutcome) => $"outcome == '{conditionOutcome}'";

    public static bool IsModelElement(XName name) => name == BpmnXmlNames.Model + ElementLocalName;

    public static bool IsModelElement(BpmnQName name) =>
        StringComparer.Ordinal.Equals(name.LocalName, ElementLocalName)
        && StringComparer.Ordinal.Equals(name.Namespace, BpmnXmlNames.Model.NamespaceName);

    /// <summary>
    /// Whether this is the library-generated predicate for <paramref name="conditionOutcome"/>. A later
    /// read must consume it rather than retain it, so changing or clearing the outcome cannot leave a
    /// contradictory expression.
    /// </summary>
    public static bool IsLibraryGenerated(XElement element, string? conditionOutcome)
    {
        if (!IsModelElement(element.Name) || string.IsNullOrWhiteSpace(conditionOutcome))
            return false;
        if (element.HasElements)
            return false;
        if (element.Attributes().Any(attribute => !attribute.IsNamespaceDeclaration))
            return false;
        return string.Equals(element.Value, GeneratedBody(conditionOutcome), StringComparison.Ordinal);
    }

    /// <summary>
    /// A retained subtree that has the writer's generated shape — <c>outcome == '…'</c>, no attributes,
    /// no children — regardless of which outcome it names.
    /// <para>
    /// Two edges follow. An author expression that only looks like the generated predicate (same
    /// shape, a body that is not <c>outcome == '&lt;current outcome&gt;'</c>) is kept on read —
    /// <see cref="IsLibraryGenerated"/> requires an exact match — and then dropped on write while
    /// the flow still has a <c>conditionOutcome</c>. A mismatched expression of that shape survives
    /// clearing the outcome: the writer only strips when an outcome is still present.
    /// </para>
    /// </summary>
    public static bool IsLibraryGeneratedShape(BpmnExtensionElement element)
    {
        if (!IsModelElement(element.Name) || element.Children.Count > 0 || element.Attributes.Count > 0)
            return false;
        return IsGeneratedBody(element.Value);
    }

    public static bool IsGeneratedBody(string? value)
    {
        const string prefix = "outcome == '";
        return value is not null
            && value.StartsWith(prefix, StringComparison.Ordinal)
            && value.Length > prefix.Length
            && value[^1] == '\'';
    }
}
