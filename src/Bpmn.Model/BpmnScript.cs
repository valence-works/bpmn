using System.Text.Json.Serialization;

namespace Bpmn.Model;

/// <summary>
/// A <c>scriptTask</c>'s script: its <c>scriptFormat</c> attribute and the body of its <c>&lt;script&gt;</c>
/// child, carried as data.
/// <para>
/// This library never parses or runs <see cref="Body"/>. A script task binds work like any other task, and
/// the host that runs that work owns the script engine; the script is modeled so a read, edit and write keeps
/// it, and so a host can find it on the element without reading the XML again.
/// </para>
/// </summary>
public sealed record BpmnScript
{
    /// <summary>Creates a script.</summary>
    /// <param name="body">The script text exactly as authored; may be empty, never null.</param>
    /// <param name="format">The task's <c>scriptFormat</c>, a MIME type such as <c>text/javascript</c>, or <c>null</c> when it declares none.</param>
    /// <param name="extensions">Foreign attributes and child elements retained from the <c>&lt;script&gt;</c> element.</param>
    [JsonConstructor]
    public BpmnScript(string body, string? format = null, BpmnExtensions? extensions = null)
    {
        ArgumentNullException.ThrowIfNull(body);

        Body = body;
        Format = string.IsNullOrWhiteSpace(format) ? null : format.Trim();
        Extensions = extensions ?? BpmnExtensions.Empty;
    }

    /// <summary>
    /// The script text: the <c>&lt;script&gt;</c> element's text and CDATA content, concatenated in document
    /// order, read the same way as <see cref="BpmnConditionExpression.Body"/>. Empty when the task declares a
    /// format but no script. Text and CDATA read the same; which of the two the source used is not preserved.
    /// </summary>
    [JsonPropertyName("body")]
    public string Body { get; }

    /// <summary>The BPMN <c>scriptFormat</c> attribute of the task, or <c>null</c> when it declares none.</summary>
    [JsonPropertyName("format")]
    public string? Format { get; }

    /// <summary>
    /// Content retained from the <c>&lt;script&gt;</c> element that has no meaning here: its attributes and any
    /// child elements, which BPMN allows on a script from any namespace. Never null.
    /// </summary>
    [JsonPropertyName("extensions")]
    public BpmnExtensions Extensions { get; }
}
