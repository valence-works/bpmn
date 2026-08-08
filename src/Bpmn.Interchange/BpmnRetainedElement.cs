using Bpmn.Model;

namespace Bpmn.Interchange;

/// <summary>
/// Foreign XML retained from one flow element, sequence flow, or collaboration.
/// <para>
/// <see cref="BpmnDefinitions"/> and <see cref="BpmnProcessDefinition"/> carry their retained content
/// directly; the model's per-element types do not, so element-scoped retention travels alongside the model
/// and is matched back by <see cref="ProcessId"/> plus <see cref="ElementId"/>. Hand the list back to
/// <see cref="BpmnXmlWriter"/> and the content is written where it came from.
/// </para>
/// </summary>
/// <param name="ProcessId">The process the element belongs to; the collaboration id for collaboration-level content.</param>
/// <param name="ElementId">The BPMN id of the element, sequence flow, or collaboration.</param>
/// <param name="Extensions">The retained content.</param>
public sealed record BpmnRetainedElement(string ProcessId, string ElementId, BpmnExtensions Extensions);
