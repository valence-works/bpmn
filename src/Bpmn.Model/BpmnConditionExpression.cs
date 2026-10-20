using System.Text.Json.Serialization;

namespace Bpmn.Model;

/// <summary>
/// A sequence flow's <c>&lt;conditionExpression&gt;</c>: an expression body in some expression language,
/// carried as data.
/// <para>
/// This library never parses or evaluates <see cref="Body"/>. ADR 0011 records how a host evaluates it
/// through a synchronous port. Until the interpreter asks that port, a flow whose only condition is an
/// expression is routed as unconditional, and the XML reader reports a <c>Degraded</c> finding saying so.
/// A flow that also carries a <see cref="BpmnSequenceFlow.ConditionOutcome"/> is matched by that outcome,
/// and its expression is retained only.
/// </para>
/// </summary>
public sealed record BpmnConditionExpression
{
    /// <summary>Creates a condition expression.</summary>
    /// <param name="body">The expression text exactly as authored; may be empty, never null.</param>
    /// <param name="language">
    /// The expression language URI declared on this expression, or <c>null</c> to inherit the process
    /// default (<see cref="BpmnProcessDefinition.ExpressionLanguage"/>).
    /// </param>
    /// <param name="extensions">
    /// Everything else on the element: <c>xsi:type</c>, <c>id</c>, foreign attributes, documentation and
    /// extension elements.
    /// </param>
    [JsonConstructor]
    public BpmnConditionExpression(string body, string? language = null, BpmnExtensions? extensions = null)
    {
        ArgumentNullException.ThrowIfNull(body);

        Body = body;
        Language = string.IsNullOrWhiteSpace(language) ? null : language.Trim();
        Extensions = extensions ?? BpmnExtensions.Empty;
    }

    /// <summary>
    /// The expression text: the element's text and CDATA content, concatenated in document order. The
    /// characters of each text run and CDATA section are kept exactly, including leading and trailing
    /// whitespace inside a run. A text node that is only whitespace, such as the indentation around a
    /// CDATA section or between child elements, is not part of the body, because the reader does not
    /// preserve insignificant whitespace. Text and CDATA read the same; which of the two the source used
    /// is not preserved.
    /// </summary>
    [JsonPropertyName("body")]
    public string Body { get; }

    /// <summary>
    /// The BPMN <c>language</c> attribute of this expression, or <c>null</c> when it declares none and so
    /// inherits <see cref="BpmnProcessDefinition.ExpressionLanguage"/>. Kept exactly as declared: the
    /// inherited value is not copied here, so a write reproduces the document as authored.
    /// </summary>
    [JsonPropertyName("language")]
    public string? Language { get; }

    /// <summary>
    /// Content retained from the expression element that has no meaning here: its <c>xsi:type</c> (a
    /// <c>bpmn:</c> prefix is normalized as it is everywhere else), its <c>id</c>, vendor attributes,
    /// documentation and extension elements. Never null.
    /// <para>
    /// A read does not retain <c>bpmn:tFormalExpression</c> on an expression that has a
    /// <see cref="Language"/>: the writer writes that type for every expression with a language, since
    /// BPMN allows a language only on a formal expression, so retaining it would make a second read differ
    /// from the first. Any other <c>xsi:type</c>, or that one on an expression without a language, is
    /// retained.
    /// </para>
    /// </summary>
    [JsonPropertyName("extensions")]
    public BpmnExtensions Extensions { get; }
}
