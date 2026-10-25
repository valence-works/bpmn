using System.Xml.Linq;
using Bpmn.Model;

namespace Bpmn.Interchange;

/// <summary>
/// Distinguishes an author's <c>conditionExpression</c> from the predicate this library writes for a
/// <c>conditionOutcome</c>. The generated form is exactly <c>outcome == 'X'</c> with no attributes and
/// no child elements — the only shape <see cref="BpmnXmlWriter"/> emits on its own.
/// </summary>
internal static class BpmnGeneratedCondition
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
        return IsGeneratedBody(element.Value, conditionOutcome);
    }

    /// <summary>
    /// The retained counterpart of <see cref="IsLibraryGenerated(XElement, string?)"/>, for a
    /// <c>conditionExpression</c> a payload serialized before format 1.3.0 carries as a foreign child.
    /// The writer drops only this exact predicate; a child of the same shape that names another outcome
    /// is the author's and is written back.
    /// </summary>
    public static bool IsLibraryGenerated(BpmnExtensionElement element, string? conditionOutcome) =>
        IsModelElement(element.Name)
        && element.Children.Count == 0
        && element.Attributes.Count == 0
        && IsGeneratedBody(element.Value, conditionOutcome);

    /// <summary>
    /// The modeled counterpart of <see cref="IsLibraryGenerated(XElement, string?)"/>: an expression with
    /// no language, no retained content, and exactly the body the writer generates for
    /// <paramref name="conditionOutcome"/>. The writer emits its own predicate in its place, once. An
    /// expression of the same shape that names another outcome is the author's (ADR 0011) and is written
    /// back.
    /// </summary>
    public static bool IsLibraryGenerated(BpmnConditionExpression expression, string? conditionOutcome) =>
        expression.Language is null
        && expression.Extensions.IsEmpty
        && IsGeneratedBody(expression.Body, conditionOutcome);

    private static bool IsGeneratedBody(string? value, string? conditionOutcome) =>
        !string.IsNullOrWhiteSpace(conditionOutcome)
        && string.Equals(value, GeneratedBody(conditionOutcome), StringComparison.Ordinal);
}
