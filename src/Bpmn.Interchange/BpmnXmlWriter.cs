using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Bpmn.Model;

namespace Bpmn.Interchange;

/// <summary>
/// Writes a <see cref="BpmnDefinitions"/> back out as BPMN 2.0 XML with BPMN DI layout.
/// <para>
/// Everything the reader retained is written where it came from: documentation, vendor extension elements,
/// foreign attributes, and unrecognized children go back into the canonical child order BPMN's schema
/// requires, so the output stays valid for other modeling tools.
/// </para>
/// <para>
/// Layout is always complete. Every element, pool, and lane gets a <c>BPMNShape</c>, and every sequence flow
/// and message flow gets a <c>BPMNEdge</c> with at least the two waypoints BPMN DI demands, synthesized from
/// the endpoint boxes when the source carried none. A flow that runs backwards is routed as an elbow below the
/// row so a cyclic graph does not render as a pile of overlapping lines.
/// </para>
/// </summary>
public sealed class BpmnXmlWriter
{
    private const double LoopBackDrop = 50d;
    private const double ContainerPadding = 40d;
    private const double ElementGap = 80d;
    private const double PoolHeaderWidth = 30d;
    private const double RowGap = 60d;

    /// <summary>Writes back exactly what a read produced, including its bindings.</summary>
    public string Write(BpmnImportResult result, BpmnExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        return Write(result.Definitions, result.Bindings, options);
    }

    /// <summary>
    /// Writes a single process in a minimal document, using the document writer's declaration and layout
    /// synthesis. <paramref name="bindings"/> supplies nested processes and event bindings. Use the
    /// <see cref="BpmnDefinitions"/> overload to retain document metadata, explicit root declarations,
    /// collaboration, or imported diagrams.
    /// </summary>
    public string Write(
        BpmnProcessDefinition process,
        IReadOnlyList<BpmnWorkBinding>? bindings = null,
        BpmnExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(process);
        return Write(new BpmnDefinitions(Processes: [process]), bindings, options);
    }

    /// <summary>
    /// Writes a document. <paramref name="bindings"/> supplies the nested process behind each subprocess
    /// element; without it, subprocesses are written as empty. Retained foreign content rides on the model
    /// itself, so nothing else has to be threaded through alongside it.
    /// </summary>
    public string Write(
        BpmnDefinitions definitions,
        IReadOnlyList<BpmnWorkBinding>? bindings = null,
        BpmnExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        if (definitions.Processes.Count == 0)
            throw new BpmnInterchangeException("The definitions carry no <process> to write.");

        var context = new WriteContext(definitions, bindings, BpmnVendorNames.For(options));
        var vendor = context.Vendor;

        var root = new XElement(BpmnXmlNames.Model + "definitions",
            new XAttribute(XNamespace.Xmlns + "bpmndi", BpmnXmlNames.Di.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "dc", BpmnXmlNames.Dc.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "di", BpmnXmlNames.Dd.NamespaceName),
            new XAttribute(XNamespace.Xmlns + vendor.Prefix, vendor.NamespaceName));

        root.SetAttributeValue("id", definitions.Id ?? $"{SanitizeId(definitions.Processes[0].ProcessId)}-definitions");
        root.SetAttributeValue("targetNamespace", options?.TargetNamespace ?? definitions.TargetNamespace ?? vendor.NamespaceName);
        var exporter = options?.Exporter ?? definitions.Exporter;
        if (!string.IsNullOrWhiteSpace(exporter)) root.SetAttributeValue("exporter", exporter);
        var exporterVersion = options?.ExporterVersion ?? definitions.ExporterVersion;
        if (!string.IsNullOrWhiteSpace(exporterVersion)) root.SetAttributeValue("exporterVersion", exporterVersion);
        if (context.ExpressionLanguage is { } expressionLanguage) root.SetAttributeValue("expressionLanguage", expressionLanguage);

        AppendCollaboration(root, definitions, context);

        foreach (var process in definitions.Processes)
            AppendProcess(root, process, context);

        // Declarations are collected last and emitted last, because writing the processes is what discovers
        // the message, signal, and escalation names that only a work binding knew about. The canonical child
        // order moves them ahead of the collaboration and processes in the output.
        AppendRootDeclarations(root, definitions, context);
        AppendDiagrams(root, definitions, context);
        ApplyExtensions(root, definitions.Extensions, vendor);
        DeclareForeignNamespaces(root, vendor);

        var document = new XDocument(root);
        if (!(options?.OmitXmlDeclaration ?? false))
            document.Declaration = new XDeclaration("1.0", "utf-8", null);

        return document.ToString();
    }

    // ---------------------------------------------------------------------------------------------------
    // Root declarations
    // ---------------------------------------------------------------------------------------------------

    private static void AppendRootDeclarations(XElement root, BpmnDefinitions definitions, WriteContext context)
    {
        foreach (var message in definitions.Messages)
            root.Add(Declaration("message", message.Id, message.Name, null, null));
        foreach (var signal in definitions.Signals)
            root.Add(Declaration("signal", signal.Id, signal.Name, null, null));
        foreach (var error in definitions.Errors)
            root.Add(Declaration("error", error.Id, error.Name, "errorCode", error.ErrorCode));
        foreach (var escalation in definitions.Escalations)
            root.Add(Declaration("escalation", escalation.Id, escalation.Name, "escalationCode", escalation.EscalationCode));

        foreach (var synthesized in context.SynthesizedDeclarations)
            root.Add(Declaration(synthesized.LocalName, synthesized.Id, synthesized.Name, synthesized.CodeAttribute, synthesized.Code));
    }

    private static XElement Declaration(string localName, string id, string? name, string? codeAttribute, string? code)
    {
        var element = new XElement(BpmnXmlNames.Model + localName, new XAttribute("id", id));
        if (!string.IsNullOrWhiteSpace(name)) element.SetAttributeValue("name", name);
        if (codeAttribute is not null && !string.IsNullOrWhiteSpace(code)) element.SetAttributeValue(codeAttribute, code);
        return element;
    }

    // ---------------------------------------------------------------------------------------------------
    // Collaboration
    // ---------------------------------------------------------------------------------------------------

    private static void AppendCollaboration(XElement root, BpmnDefinitions definitions, WriteContext context)
    {
        if (definitions.Collaboration is not { } collaboration)
            return;
        if (collaboration.Pools.Count == 0 && collaboration.MessageFlows.Count == 0)
            return;

        var collaborationId = collaboration.Id ?? $"{SanitizeId(definitions.Processes[0].ProcessId)}-collaboration";
        var element = new XElement(BpmnXmlNames.Model + "collaboration", new XAttribute("id", collaborationId));

        var seenPools = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pool in collaboration.Pools)
        {
            if (!seenPools.Add(pool.PoolId)) continue;
            var participant = new XElement(BpmnXmlNames.Model + "participant", new XAttribute("id", pool.PoolId));
            if (pool.Name is not null) participant.SetAttributeValue("name", pool.Name);
            if (pool.ProcessRef is not null) participant.SetAttributeValue("processRef", pool.ProcessRef);
            ApplyExtensions(participant, pool.Extensions, context.Vendor);
            element.Add(participant);
        }

        var seenFlows = new HashSet<string>(StringComparer.Ordinal);
        foreach (var flow in collaboration.MessageFlows)
        {
            var sourceRef = flow.SourceElementId ?? flow.SourcePoolId;
            var targetRef = flow.TargetElementId ?? flow.TargetPoolId;
            if (string.IsNullOrWhiteSpace(sourceRef) || string.IsNullOrWhiteSpace(targetRef) || !seenFlows.Add(flow.FlowId))
                continue;

            var messageFlow = new XElement(BpmnXmlNames.Model + "messageFlow",
                new XAttribute("id", flow.FlowId),
                new XAttribute("sourceRef", sourceRef),
                new XAttribute("targetRef", targetRef));
            if (flow.Name is not null) messageFlow.SetAttributeValue("name", flow.Name);
            if (flow.MessageName is { } messageName) messageFlow.SetAttributeValue("messageRef", context.MessageDeclarationId(messageName));
            ApplyExtensions(messageFlow, flow.Extensions, context.Vendor);
            element.Add(messageFlow);
        }

        ApplyExtensions(element, collaboration.Extensions, context.Vendor);
        root.Add(element);
    }

    // ---------------------------------------------------------------------------------------------------
    // Processes
    // ---------------------------------------------------------------------------------------------------

    private static void AppendProcess(XElement root, BpmnProcessDefinition process, WriteContext context)
    {
        var element = new XElement(BpmnXmlNames.Model + "process", new XAttribute("id", process.ProcessId));
        if (process.Name is not null) element.SetAttributeValue("name", process.Name);
        element.SetAttributeValue("isExecutable", process.IsExecutable ? "true" : "false");
        AppendContainerContent(element, process, context);
        root.Add(element);
    }

    private static void AppendContainerContent(XElement container, BpmnProcessDefinition process, WriteContext context, bool isEventSubprocessBody = false)
    {
        if (process.Lanes.Count > 0)
        {
            var laneSet = new XElement(BpmnXmlNames.Model + "laneSet", new XAttribute("id", $"{SanitizeId(process.ProcessId)}-lanes"));
            foreach (var lane in process.Lanes)
            {
                var laneElement = new XElement(BpmnXmlNames.Model + "lane", new XAttribute("id", lane.LaneId));
                if (lane.Name is not null) laneElement.SetAttributeValue("name", lane.Name);
                foreach (var member in process.Elements.Where(element => StringComparer.Ordinal.Equals(element.LaneId, lane.LaneId)))
                    laneElement.Add(new XElement(BpmnXmlNames.Model + "flowNodeRef", member.ElementId));
                ApplyExtensions(laneElement, lane.Extensions, context.Vendor);
                laneSet.Add(laneElement);
            }

            container.Add(laneSet);
        }

        // <incoming> and <outgoing> are derived from the sequence flows rather than stored, and are emitted
        // because strict readers expect a flow node to list its own connections.
        var incoming = process.SequenceFlows.ToLookup(flow => flow.TargetRef, flow => flow.FlowId, StringComparer.Ordinal);
        var outgoing = process.SequenceFlows.ToLookup(flow => flow.SourceRef, flow => flow.FlowId, StringComparer.Ordinal);

        foreach (var element in process.Elements)
        {
            var xmlElement = element.ElementType switch
            {
                BpmnElementTypes.StartEvent => BuildEventElement("startEvent", element, context, isCatch: false, isEventSubprocessBodyStart: isEventSubprocessBody),
                BpmnElementTypes.IntermediateCatchEvent => BuildEventElement("intermediateCatchEvent", element, context, isCatch: true),
                BpmnElementTypes.IntermediateThrowEvent => BuildThrowEvent(element, context),
                BpmnElementTypes.EndEvent => BuildEndEvent(element, context),
                BpmnElementTypes.SubProcess => BuildSubProcess(element, context),
                BpmnElementTypes.CallActivity => BuildCallActivity(element, context),
                BpmnElementTypes.BoundaryEvent => BuildBoundaryEvent(element, context),
                BpmnElementTypes.SendTask => BuildMessageTask("sendTask", element, context),
                BpmnElementTypes.ReceiveTask => BuildMessageTask("receiveTask", element, context),
                _ => new XElement(BpmnXmlNames.Model + element.ElementType)
            };

            xmlElement.SetAttributeValue("id", element.ElementId);
            if (element.Name is not null) xmlElement.SetAttributeValue("name", element.Name);
            if (element.DefaultFlowId is not null) xmlElement.SetAttributeValue("default", element.DefaultFlowId);
            if (element.IsForCompensation) xmlElement.SetAttributeValue("isForCompensation", "true");
            foreach (var flowId in incoming[element.ElementId])
                InsertByRank(xmlElement, new XElement(BpmnXmlNames.Model + "incoming", flowId));
            foreach (var flowId in outgoing[element.ElementId])
                InsertByRank(xmlElement, new XElement(BpmnXmlNames.Model + "outgoing", flowId));
            AppendLoopCharacteristics(xmlElement, element, context.Vendor);
            if (element.Script is not null && StringComparer.Ordinal.Equals(element.ElementType, BpmnElementTypes.ScriptTask))
                AppendScript(xmlElement, element.Script, context);

            // A subprocess already had the nested process's retained content applied while its body was written.
            if (!StringComparer.Ordinal.Equals(element.ElementType, BpmnElementTypes.SubProcess))
                ApplyExtensions(xmlElement, WithoutRetainedScript(element), context.Vendor);

            container.Add(xmlElement);
        }

        foreach (var flow in process.SequenceFlows)
        {
            var flowElement = new XElement(BpmnXmlNames.Model + "sequenceFlow",
                new XAttribute("id", flow.FlowId),
                new XAttribute("sourceRef", flow.SourceRef),
                new XAttribute("targetRef", flow.TargetRef));
            if (flow.Name is not null) flowElement.SetAttributeValue("name", flow.Name);
            var extensions = WithoutLibraryConditionExpression(flow.Extensions, flow.ConditionOutcome);
            // Only the exact predicate for this flow's own outcome is the library's. An expression of the same
            // shape that names another outcome is the author's, and is written back (ADR 0011).
            var authoredExpression = flow.ConditionExpression is { } candidate
                && BpmnGeneratedCondition.IsLibraryGenerated(candidate, flow.ConditionOutcome)
                    ? null
                    : flow.ConditionExpression;
            if (authoredExpression is { } conditionExpression)
            {
                // BPMN allows one conditionExpression. The modeled one wins over a copy a pre-1.3.0 payload
                // still carries as a retained foreign child, which would otherwise be written beside it. A read
                // never retains one: it models the first and reports any further one as dropped.
                extensions = WithoutRetainedConditionExpression(extensions);
                flowElement.Add(ConditionExpressionElement(conditionExpression, process.ExpressionLanguage, context));
            }

            if (flow.ConditionOutcome is not null)
            {
                // BPMN has no outcome-matched condition, so the match rides a vendor attribute. A
                // synthesized conditionExpression is only for models that never had one — typically a
                // process built in code — so other modelers still see a readable predicate. An author's
                // expression is written above, or restored below when an older payload retained it as a
                // foreign child; either way it is the flow's one conditionExpression. The library's own
                // predicate for this outcome is stripped first and written here once.
                flowElement.SetAttributeValue(context.Vendor.ConditionOutcome, flow.ConditionOutcome);
                if (authoredExpression is null && !HasAuthorConditionExpression(extensions))
                    flowElement.Add(new XElement(BpmnXmlNames.Model + "conditionExpression", BpmnGeneratedCondition.GeneratedBody(flow.ConditionOutcome)));
            }

            ApplyExtensions(flowElement, extensions, context.Vendor);
            container.Add(flowElement);
        }

        // A compensation boundary's link to its handler is an <association>, which is how the reader finds it again.
        foreach (var boundary in process.Elements.Where(element => element.CompensationHandlerElementId is not null))
            container.Add(new XElement(BpmnXmlNames.Model + "association",
                new XAttribute("id", $"{boundary.ElementId}-assoc"),
                new XAttribute("associationDirection", "One"),
                new XAttribute("sourceRef", boundary.ElementId),
                new XAttribute("targetRef", boundary.CompensationHandlerElementId!)));

        var variableDeclarations = process.Variables
            .Select(variable =>
            {
                var declaration = new XElement(context.Vendor.Variable, new XAttribute("name", variable.Name));
                if (variable.TypeHint is not null) declaration.SetAttributeValue("typeHint", variable.TypeHint);
                return declaration;
            })
            .ToArray();

        ApplyExtensions(container, process.Extensions, context.Vendor, variableDeclarations);
    }

    /// <summary>
    /// Emits a <c>&lt;multiInstanceLoopCharacteristics&gt;</c> for an activity that carries them: either a
    /// literal <c>&lt;loopCardinality&gt;</c>, or the vendor collection and item-variable attributes.
    /// </summary>
    private static void AppendLoopCharacteristics(XElement host, BpmnElement element, BpmnVendorNames vendor)
    {
        if (element.LoopCharacteristics is not { } loop)
            return;

        var multiInstance = new XElement(BpmnXmlNames.Model + "multiInstanceLoopCharacteristics",
            new XAttribute("isSequential", loop.IsSequential ? "true" : "false"));

        if (loop.Cardinality is { } cardinality)
            multiInstance.Add(new XElement(BpmnXmlNames.Model + "loopCardinality", cardinality.ToString(CultureInfo.InvariantCulture)));
        else if (loop.CollectionVariable is { } collection)
        {
            multiInstance.SetAttributeValue(vendor.Collection, collection);
            multiInstance.SetAttributeValue(vendor.ItemVariable, loop.ItemVariable);
        }

        host.Add(multiInstance);
    }

    private static XElement BuildEndEvent(BpmnElement element, WriteContext context)
    {
        var endEvent = new XElement(BpmnXmlNames.Model + "endEvent");
        if (HasDefinition(element, BpmnEventDefinitionTypes.Terminate))
            endEvent.Add(new XElement(BpmnXmlNames.Model + "terminateEventDefinition"));
        else if (FindDefinition(element, BpmnEventDefinitionTypes.Compensation) is { } compensation)
            endEvent.Add(BuildCompensateEventDefinition(compensation));
        else if (HasDefinition(element, BpmnEventDefinitionTypes.Cancel))
            endEvent.Add(new XElement(BpmnXmlNames.Model + "cancelEventDefinition"));
        else if (FindDefinition(element, BpmnEventDefinitionTypes.Escalation) is { } escalation)
            endEvent.Add(BuildEscalationEventDefinition(escalation, context));
        else if (FindDefinition(element, BpmnEventDefinitionTypes.Error) is { } error)
            endEvent.Add(BuildErrorEventDefinition(element, error, context));
        else if (FindDefinition(element, BpmnEventDefinitionTypes.Message) is { } message)
            AppendEventDefinition(endEvent, element, message, context, isCatch: false);
        else if (FindDefinition(element, BpmnEventDefinitionTypes.Signal) is { } signal)
            AppendEventDefinition(endEvent, element, signal, context, isCatch: false);
        return endEvent;
    }

    private static XElement BuildThrowEvent(BpmnElement element, WriteContext context)
    {
        var throwEvent = new XElement(BpmnXmlNames.Model + "intermediateThrowEvent");
        if (FindDefinition(element, BpmnEventDefinitionTypes.Compensation) is { } compensation)
            throwEvent.Add(BuildCompensateEventDefinition(compensation));
        else if (FindDefinition(element, BpmnEventDefinitionTypes.Escalation) is { } escalation)
            throwEvent.Add(BuildEscalationEventDefinition(escalation, context));
        else if (FindDefinition(element, BpmnEventDefinitionTypes.Message) is { } message)
            AppendEventDefinition(throwEvent, element, message, context, isCatch: false);
        else if (FindDefinition(element, BpmnEventDefinitionTypes.Signal) is { } signal)
            AppendEventDefinition(throwEvent, element, signal, context, isCatch: false);
        else if (FindDefinition(element, BpmnEventDefinitionTypes.Link) is { } link)
            AppendEventDefinition(throwEvent, element, link, context, isCatch: false);
        return throwEvent;
    }

    /// <summary>An <c>&lt;escalationEventDefinition&gt;</c>, pointing at the root declaration for its code; ref-less for a code-less catch-all boundary.</summary>
    private static XElement BuildEscalationEventDefinition(BpmnEventDefinition definition, WriteContext context)
    {
        var escalation = new XElement(BpmnXmlNames.Model + "escalationEventDefinition");
        if (definition.Properties.TryGetValue(BpmnEventDefinitionProperties.Code, out var code) && !string.IsNullOrWhiteSpace(code))
            escalation.SetAttributeValue("escalationRef", context.EscalationDeclarationId(code.Trim()));
        return escalation;
    }

    /// <summary>
    /// An <c>&lt;errorEventDefinition&gt;</c>. The <c>errorRef</c> a read recorded on the element wins, so an
    /// author's declaration ids survive; a model built in code that carries only a code points at the root
    /// declaration for that code. Ref-less when there is neither: a catch-all catcher or an uncoded throw.
    /// </summary>
    private static XElement BuildErrorEventDefinition(BpmnElement element, BpmnEventDefinition definition, WriteContext context)
    {
        var error = new XElement(BpmnXmlNames.Model + "errorEventDefinition");
        if (element.Properties.TryGetValue(BpmnXmlReader.ErrorRefPropertyKey, out var errorRef) && !string.IsNullOrWhiteSpace(errorRef))
            error.SetAttributeValue("errorRef", errorRef.Trim());
        else if (definition.Properties.TryGetValue(BpmnEventDefinitionProperties.Code, out var code) && !string.IsNullOrWhiteSpace(code))
            error.SetAttributeValue("errorRef", context.ErrorDeclarationId(code.Trim()));
        return error;
    }

    private static XElement BuildCompensateEventDefinition(BpmnEventDefinition definition)
    {
        var compensate = new XElement(BpmnXmlNames.Model + "compensateEventDefinition");
        if (definition.Properties.TryGetValue(BpmnEventDefinitionProperties.ActivityRef, out var activityRef) && !string.IsNullOrWhiteSpace(activityRef))
            compensate.SetAttributeValue("activityRef", activityRef.Trim());
        return compensate;
    }

    /// <summary>
    /// A start or intermediate catch event, emitting its event definition. An event-subprocess body start
    /// additionally declares <c>isInterrupting="false"</c> when non-interrupting, and uses the one-shot
    /// <c>&lt;timeDuration&gt;</c> shape rather than the recurring <c>&lt;timeCycle&gt;</c> a process-level
    /// timer start uses.
    /// </summary>
    private static XElement BuildEventElement(string localName, BpmnElement element, WriteContext context, bool isCatch, bool isEventSubprocessBodyStart = false)
    {
        var xmlElement = new XElement(BpmnXmlNames.Model + localName);
        if (element.EventDefinitions.FirstOrDefault() is { } definition)
        {
            if (StringComparer.Ordinal.Equals(definition.Type, BpmnEventDefinitionTypes.Escalation))
            {
                xmlElement.Add(BuildEscalationEventDefinition(definition, context));
                if (!element.CancelActivity) xmlElement.SetAttributeValue("isInterrupting", "false");
            }
            else if (StringComparer.Ordinal.Equals(definition.Type, BpmnEventDefinitionTypes.Error))
            {
                xmlElement.Add(BuildErrorEventDefinition(element, definition, context));
                if (!element.CancelActivity) xmlElement.SetAttributeValue("isInterrupting", "false");
            }
            else if (isEventSubprocessBodyStart)
            {
                AppendEventDefinition(xmlElement, element, definition, context, isCatch: true);
                if (!element.CancelActivity) xmlElement.SetAttributeValue("isInterrupting", "false");
            }
            else
            {
                AppendEventDefinition(xmlElement, element, definition, context, isCatch);
            }
        }

        return xmlElement;
    }

    /// <summary>
    /// A boundary event: <c>attachedToRef</c>, <c>cancelActivity="false"</c> only when non-interrupting
    /// (absent means interrupting, which is the BPMN default), and its single event definition.
    /// </summary>
    private static XElement BuildBoundaryEvent(BpmnElement element, WriteContext context)
    {
        var boundary = new XElement(BpmnXmlNames.Model + "boundaryEvent");
        if (element.AttachedToRef is not null)
            boundary.SetAttributeValue("attachedToRef", element.AttachedToRef);
        if (!element.CancelActivity)
            boundary.SetAttributeValue("cancelActivity", "false");

        if (element.EventDefinitions.FirstOrDefault() is { } definition)
        {
            if (StringComparer.Ordinal.Equals(definition.Type, BpmnEventDefinitionTypes.Error))
                boundary.Add(BuildErrorEventDefinition(element, definition, context));
            else if (StringComparer.Ordinal.Equals(definition.Type, BpmnEventDefinitionTypes.Escalation))
                boundary.Add(BuildEscalationEventDefinition(definition, context));
            else if (StringComparer.Ordinal.Equals(definition.Type, BpmnEventDefinitionTypes.Compensation))
                boundary.Add(new XElement(BpmnXmlNames.Model + "compensateEventDefinition"));
            else if (StringComparer.Ordinal.Equals(definition.Type, BpmnEventDefinitionTypes.Cancel))
                boundary.Add(new XElement(BpmnXmlNames.Model + "cancelEventDefinition"));
            else
                AppendEventDefinition(boundary, element, definition, context, isCatch: true);
        }

        return boundary;
    }

    /// <summary>
    /// Emits an event definition. The detail can live in either of two places: the event definition's own
    /// properties, which is where a read puts it, or the element's <see cref="BpmnWorkBinding"/>, which is
    /// where a model built in code puts it. Both are consulted.
    /// <para>
    /// The element is emitted even when neither supplies the detail. Dropping it instead would turn a timer
    /// boundary into a bare boundary and a message end into a plain end - a silent change of meaning that the
    /// reader then reports as "declares 0 event definitions". An incomplete definition at least round-trips
    /// into an accurate finding about what is missing.
    /// </para>
    /// </summary>
    private static void AppendEventDefinition(XElement host, BpmnElement element, BpmnEventDefinition definition, WriteContext context, bool isCatch)
    {
        switch (definition.Type)
        {
            case BpmnEventDefinitionTypes.Message:
            case BpmnEventDefinitionTypes.Signal:
            {
                var isMessage = definition.Type == BpmnEventDefinitionTypes.Message;
                var name = Property(definition, BpmnEventDefinitionProperties.Name) ?? context.EventNameFor(element, isMessage);
                var child = new XElement(BpmnXmlNames.Model + (isMessage ? "messageEventDefinition" : "signalEventDefinition"));
                if (name is not null)
                    child.SetAttributeValue(
                        isMessage ? "messageRef" : "signalRef",
                        isMessage ? context.MessageDeclarationId(name) : context.SignalDeclarationId(name));
                host.Add(child);
                break;
            }
            case BpmnEventDefinitionTypes.Link:
            {
                var link = new XElement(BpmnXmlNames.Model + "linkEventDefinition");
                if (Property(definition, BpmnEventDefinitionProperties.Name) is { } linkName)
                    link.SetAttributeValue("name", linkName);
                host.Add(link);
                break;
            }
            case BpmnEventDefinitionTypes.Timer:
            {
                var timer = new XElement(BpmnXmlNames.Model + "timerEventDefinition");
                var interval = Property(definition, BpmnEventDefinitionProperties.Interval) ?? context.TimerDurationFor(element);
                var cron = Property(definition, BpmnEventDefinitionProperties.Cron);
                var date = Property(definition, BpmnEventDefinitionProperties.Date) ?? (isCatch ? context.TimerDateFor(element) : null);
                var cycle = Property(definition, BpmnEventDefinitionProperties.Cycle) ?? (isCatch ? context.TimerCycleFor(element) : null);

                if (cycle is not null)
                {
                    // A cycle at any position, written back exactly as it was read.
                    timer.Add(new XElement(BpmnXmlNames.Model + "timeCycle", cycle));
                }
                else if (date is not null)
                {
                    // A timer at an instant, one-shot at every position: a start, a catch, a boundary or an
                    // event-subprocess start.
                    timer.Add(new XElement(BpmnXmlNames.Model + "timeDate", date));
                }
                else if (isCatch)
                {
                    // A catching timer is a one-shot relative delay.
                    if (interval is not null) timer.Add(new XElement(BpmnXmlNames.Model + "timeDuration", interval));
                }
                else if (cron is not null)
                {
                    timer.Add(new XElement(BpmnXmlNames.Model + "timeCycle", cron));
                }
                else if (interval is not null)
                {
                    // A recurring start interval, which reads back as an interval because of its P/R prefix.
                    timer.Add(new XElement(BpmnXmlNames.Model + "timeCycle", interval));
                }

                host.Add(timer);
                break;
            }
        }
    }

    private static string? Property(BpmnEventDefinition definition, string key) =>
        definition.Properties.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    /// <summary>
    /// A <c>&lt;callActivity&gt;</c>: <c>calledElement</c> from the element's recorded property or its call
    /// binding, plus <c>vw:waitForCompletion="false"</c> when the call is fire-and-forget, which BPMN has no
    /// standard way to say.
    /// </summary>
    private static XElement BuildCallActivity(BpmnElement element, WriteContext context)
    {
        var callActivity = new XElement(BpmnXmlNames.Model + "callActivity");
        var binding = element.BindingRef is { } bindingRef ? context.CallBinding(bindingRef) : null;

        var calledElement = element.Properties.TryGetValue(BpmnXmlReader.CalledElementPropertyKey, out var recorded) && !string.IsNullOrWhiteSpace(recorded)
            ? recorded
            : binding?.CalledElement;
        if (!string.IsNullOrWhiteSpace(calledElement))
            callActivity.SetAttributeValue("calledElement", calledElement);
        if (binding is { WaitForCompletion: false })
            callActivity.SetAttributeValue(context.Vendor.WaitForCompletion, "false");

        return callActivity;
    }

    /// <summary>A <c>&lt;sendTask&gt;</c> or <c>&lt;receiveTask&gt;</c>, with <c>messageRef</c> when the element names a message.</summary>
    private static XElement BuildMessageTask(string localName, BpmnElement element, WriteContext context)
    {
        var task = new XElement(BpmnXmlNames.Model + localName);
        var name = element.Properties.TryGetValue(BpmnXmlReader.MessageNamePropertyKey, out var recorded) && !string.IsNullOrWhiteSpace(recorded)
            ? recorded.Trim()
            : context.EventNameFor(element, isMessage: true);
        if (name is not null)
            task.SetAttributeValue("messageRef", context.MessageDeclarationId(name));
        return task;
    }

    private static XElement BuildSubProcess(BpmnElement element, WriteContext context)
    {
        var subProcess = new XElement(BpmnXmlNames.Model + (element.IsTransaction ? "transaction" : "subProcess"));
        if (element.TriggeredByEvent)
            subProcess.SetAttributeValue("triggeredByEvent", "true");

        if (element.BindingRef is { } bindingRef && context.NestedProcess(bindingRef) is { } nested)
            AppendContainerContent(subProcess, nested, context, isEventSubprocessBody: element.TriggeredByEvent);

        return subProcess;
    }

    // ---------------------------------------------------------------------------------------------------
    // Retained content and canonical ordering
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Puts an element's children into the order BPMN's schema requires, folds in the retained documentation
    /// and extension elements, restores each retained foreign child at the position it held, and re-applies
    /// the retained foreign attributes.
    /// <para>
    /// Retained content never overwrites and never duplicates what the model already produced, and is
    /// otherwise always written. The two can collide only across a change of vendor namespace, where a
    /// document read under one namespace retains the other one's names as foreign; the model is the source of
    /// truth for a name this write interprets, and retention is the source of truth for everything else.
    /// </para>
    /// </summary>
    private static void ApplyExtensions(XElement target, BpmnExtensions? extensions, BpmnVendorNames vendor, IReadOnlyList<XElement>? vendorExtensionElements = null)
    {
        extensions ??= BpmnExtensions.Empty;
        var hasVendorContent = vendorExtensionElements is { Count: > 0 };
        if (extensions.IsEmpty && !hasVendorContent && target.Elements().Count() < 2)
            return;

        var existing = target.Elements().ToList();
        foreach (var child in existing)
            child.Remove();

        var children = new List<XElement>();

        foreach (var documentation in extensions.Documentation)
        {
            var element = new XElement(BpmnXmlNames.Model + "documentation", documentation.Text);
            if (documentation.TextFormat is { } textFormat) element.SetAttributeValue("textFormat", textFormat);
            children.Add(element);
        }

        var extensionChildren = new List<XElement>();
        if (hasVendorContent) extensionChildren.AddRange(vendorExtensionElements!);
        var written = extensionChildren.Select(Identity).ToHashSet();
        foreach (var retained in extensions.ExtensionElements.Select(BpmnExtensionCapture.ToXml))
        {
            // Only a name this write interprets can already be there: the model wrote it just above.
            if (vendor.IsInterpreted(retained.Name) && !written.Add(Identity(retained))) continue;
            extensionChildren.Add(retained);
        }

        if (extensionChildren.Count > 0)
            children.Add(new XElement(BpmnXmlNames.Model + "extensionElements", extensionChildren));

        children.AddRange(existing);

        var ordered = children
            .Select((element, index) => (element, index))
            .OrderBy(entry => BpmnChildOrder.RankOf(entry.element.Name))
            .ThenBy(entry => entry.index)
            .Select(entry => entry.element)
            .ToList();

        foreach (var foreign in extensions.ForeignChildren.OrderBy(child => child.Index))
            ordered.Insert(Math.Clamp(foreign.Index, 0, ordered.Count), BpmnExtensionCapture.ToXml(foreign.Element));

        foreach (var element in ordered)
            target.Add(element);

        foreach (var attribute in extensions.ForeignAttributes)
        {
            var name = BpmnExtensionCapture.ToXName(attribute.Name);
            if (target.Attribute(name) is not null) continue;
            BpmnExtensionCapture.WriteAttribute(target, attribute);
        }
    }

    /// <summary>
    /// What makes two extension elements the same declaration: the qualified name plus the <c>name</c> the
    /// library's own vendor elements are keyed by. Enough to tell a retained <c>variable</c> that duplicates
    /// one the model wrote from a retained <c>variable</c> that declares a different one.
    /// </summary>
    private static (XName Name, string? Key) Identity(XElement element) => (element.Name, (string?)element.Attribute("name"));

    // ---------------------------------------------------------------------------------------------------
    // Diagram interchange
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Writes the diagram interchange.
    /// <para>
    /// Shapes and edges are emitted in one canonical order derived from the model - pools, then lanes, then
    /// flow elements, then the flows between them - rather than in whatever order the source document
    /// happened to use. Preserving the source order sounds more faithful but is not stable: a synthesized
    /// shape has to be appended somewhere, reading the result groups it with the other shapes, and the next
    /// write moves it. Two generations then differ by position alone, which makes the output undiffable and
    /// hides real edits in reserialization noise. Retained bounds and labels are still used exactly as
    /// found; only the position in the file is normalized.
    /// </para>
    /// </summary>
    private static void AppendDiagrams(XElement root, BpmnDefinitions definitions, WriteContext context)
    {
        var layout = new DiagramLayout(definitions, context);

        if (definitions.Diagrams.Count == 0)
        {
            var planeRef = definitions.Collaboration?.Id ?? definitions.Processes[0].ProcessId;
            var plane = new XElement(BpmnXmlNames.Di + "BPMNPlane",
                new XAttribute("id", $"{SanitizeId(planeRef)}-plane"),
                new XAttribute("bpmnElement", planeRef));
            AppendPlaneContent(plane, layout.AllShapes(), layout.AllEdges(), layout);
            root.Add(new XElement(BpmnXmlNames.Di + "BPMNDiagram", new XAttribute("id", $"{SanitizeId(planeRef)}-diagram"), plane));
            return;
        }

        var covered = definitions.Diagrams
            .SelectMany(diagram => diagram.Plane.Shapes.Select(shape => shape.BpmnElementRef)
                .Concat(diagram.Plane.Edges.Select(edge => edge.BpmnElementRef)))
            .ToHashSet(StringComparer.Ordinal);

        var first = true;
        foreach (var diagram in definitions.Diagrams)
        {
            var plane = new XElement(BpmnXmlNames.Di + "BPMNPlane",
                new XAttribute("id", diagram.Plane.Id ?? $"{SanitizeId(diagram.Plane.BpmnElementRef ?? definitions.Processes[0].ProcessId)}-plane"));
            if (diagram.Plane.BpmnElementRef is { } planeRef)
                plane.SetAttributeValue("bpmnElement", planeRef);

            var shapes = diagram.Plane.Shapes.ToList();
            var edges = diagram.Plane.Edges.Select(layout.Repair).ToList();

            // Anything the document never laid out joins the first plane, then sorts into place with the rest.
            if (first)
            {
                shapes.AddRange(layout.AllShapes().Where(shape => !covered.Contains(shape.BpmnElementRef)));
                edges.AddRange(layout.AllEdges().Where(edge => !covered.Contains(edge.BpmnElementRef)));
                first = false;
            }

            AppendPlaneContent(plane, shapes, edges, layout);

            root.Add(new XElement(BpmnXmlNames.Di + "BPMNDiagram",
                new XAttribute("id", diagram.Id ?? $"{SanitizeId(definitions.Processes[0].ProcessId)}-diagram"),
                diagram.Name is null ? null : new XAttribute("name", diagram.Name),
                plane));
        }
    }

    /// <summary>Emits every shape, then every edge, each group in canonical order.</summary>
    private static void AppendPlaneContent(XElement plane, IEnumerable<BpmnShape> shapes, IEnumerable<BpmnEdge> edges, DiagramLayout layout)
    {
        foreach (var shape in layout.InCanonicalOrder(shapes, shape => shape.BpmnElementRef))
            plane.Add(ShapeElement(shape));
        foreach (var edge in layout.InCanonicalOrder(edges, edge => edge.BpmnElementRef))
            plane.Add(EdgeElement(edge));
    }

    private static XElement ShapeElement(BpmnShape shape)
    {
        var element = new XElement(BpmnXmlNames.Di + "BPMNShape",
            new XAttribute("id", shape.Id ?? $"{SanitizeId(shape.BpmnElementRef)}_di"),
            new XAttribute("bpmnElement", shape.BpmnElementRef));
        if (shape.IsHorizontal is { } isHorizontal) element.SetAttributeValue("isHorizontal", isHorizontal ? "true" : "false");
        if (shape.IsExpanded is { } isExpanded) element.SetAttributeValue("isExpanded", isExpanded ? "true" : "false");
        if (shape.IsMarkerVisible is { } isMarkerVisible) element.SetAttributeValue("isMarkerVisible", isMarkerVisible ? "true" : "false");
        element.Add(BoundsElement(shape.Bounds));
        if (shape.Label is { } label) element.Add(LabelElement(label));
        return element;
    }

    private static XElement EdgeElement(BpmnEdge edge)
    {
        var element = new XElement(BpmnXmlNames.Di + "BPMNEdge",
            new XAttribute("id", edge.Id ?? $"{SanitizeId(edge.BpmnElementRef)}_di"),
            new XAttribute("bpmnElement", edge.BpmnElementRef));
        foreach (var waypoint in edge.Waypoints)
            element.Add(new XElement(BpmnXmlNames.Dd + "waypoint",
                new XAttribute("x", waypoint.X),
                new XAttribute("y", waypoint.Y)));
        if (edge.Label is { } label) element.Add(LabelElement(label));
        return element;
    }

    private static XElement BoundsElement(BpmnBounds bounds) =>
        new(BpmnXmlNames.Dc + "Bounds",
            new XAttribute("x", bounds.X),
            new XAttribute("y", bounds.Y),
            new XAttribute("width", bounds.Width),
            new XAttribute("height", bounds.Height));

    private static XElement LabelElement(BpmnLabel label)
    {
        var element = new XElement(BpmnXmlNames.Di + "BPMNLabel");
        if (label.Bounds is { } bounds) element.Add(BoundsElement(bounds));
        return element;
    }

    // ---------------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Drops a retained <c>conditionExpression</c> that is exactly the library's predicate for
    /// <paramref name="conditionOutcome"/>, so it is written once rather than twice.
    /// <para>
    /// A retained child of the same shape that names another outcome is the author's and is kept: the
    /// reader that serialized it had already consumed the predicate for the outcome it read, so what it
    /// retained was never the library's own.
    /// </para>
    /// </summary>
    private static BpmnExtensions WithoutLibraryConditionExpression(BpmnExtensions? extensions, string? conditionOutcome)
    {
        extensions ??= BpmnExtensions.Empty;
        if (extensions.ForeignChildren.Count == 0)
            return extensions;

        if (conditionOutcome is null)
            return extensions;

        var kept = extensions.ForeignChildren
            .Where(child => !BpmnGeneratedCondition.IsLibraryGenerated(child.Element, conditionOutcome))
            .ToList();
        return kept.Count == extensions.ForeignChildren.Count
            ? extensions
            : extensions with { ForeignChildren = kept };
    }

    /// <summary>
    /// Writes a modeled <c>conditionExpression</c>: body, <c>language</c>, and the retained rest.
    /// <para>
    /// Two attributes are supplied when the model does not carry them. A <c>language</c> is written for an
    /// expression that inherits one, when its process's default differs from the document's, so the
    /// expression keeps its meaning after the process is written into a document declaring another default.
    /// And BPMN only allows <c>language</c> on a formal expression, so an expression that has a language but
    /// no retained <c>xsi:type</c> is written as <c>bpmn:tFormalExpression</c>. The reader does not retain
    /// that type on an expression with a language, so the next read models what this one was given. An
    /// expression with neither, typically one built in code, is written as a plain expression, exactly as
    /// modeled.
    /// </para>
    /// </summary>
    private static XElement ConditionExpressionElement(BpmnConditionExpression expression, string? processExpressionLanguage, WriteContext context)
    {
        var element = new XElement(BpmnXmlNames.Model + BpmnGeneratedCondition.ElementLocalName);

        var inherited = string.IsNullOrWhiteSpace(processExpressionLanguage) ? null : processExpressionLanguage.Trim();
        var language = expression.Language
            ?? (inherited is not null && !StringComparer.Ordinal.Equals(inherited, context.ExpressionLanguage) ? inherited : null);
        if (language is not null)
            element.SetAttributeValue("language", language);

        foreach (var attribute in expression.Extensions.ForeignAttributes)
            BpmnExtensionCapture.WriteAttribute(element, attribute);

        if (language is not null && element.Attribute(BpmnXmlNames.SchemaInstance + "type") is null)
            BpmnExtensionCapture.WriteTypeAttribute(element, "bpmn:tFormalExpression");

        ApplyExtensions(element, expression.Extensions with { ForeignAttributes = [] }, context.Vendor);
        element.Add(new XText(expression.Body));
        return element;
    }

    /// <summary>
    /// Writes a script task's <c>scriptFormat</c>, and its <c>&lt;script&gt;</c> when the script has a body or
    /// retained content. A format-only script, which is what a read of a task without a <c>&lt;script&gt;</c>
    /// produces, writes no element, so the next read models the same script.
    /// </summary>
    private static void AppendScript(XElement scriptTask, BpmnScript script, WriteContext context)
    {
        if (script.Format is not null)
            scriptTask.SetAttributeValue("scriptFormat", script.Format);
        if (script.Body.Length == 0 && script.Extensions.IsEmpty)
            return;

        var element = new XElement(BpmnXmlNames.Model + "script");
        foreach (var attribute in script.Extensions.ForeignAttributes)
            BpmnExtensionCapture.WriteAttribute(element, attribute);

        ApplyExtensions(element, script.Extensions with { ForeignAttributes = [] }, context.Vendor);
        element.Add(new XText(script.Body));
        InsertByRank(scriptTask, element);
    }

    /// <summary>
    /// Drops a <c>&lt;script&gt;</c> a pre-1.6.0 payload retained as a foreign child, for a script task whose
    /// script is now modeled, so only one is written.
    /// </summary>
    private static BpmnExtensions WithoutRetainedScript(BpmnElement element)
    {
        if (element.Script is null || !StringComparer.Ordinal.Equals(element.ElementType, BpmnElementTypes.ScriptTask))
            return element.Extensions;

        return element.Extensions with
        {
            ForeignChildren = element.Extensions.ForeignChildren
                .Where(child => BpmnExtensionCapture.ToXName(child.Element.Name) != BpmnXmlNames.Model + "script")
                .ToList()
        };
    }

    /// <summary>
    /// Drops a <c>conditionExpression</c> a pre-1.3.0 payload retained as a foreign child, for a flow whose
    /// expression is now modeled.
    /// </summary>
    private static BpmnExtensions WithoutRetainedConditionExpression(BpmnExtensions extensions)
    {
        if (!HasAuthorConditionExpression(extensions))
            return extensions;

        return extensions with
        {
            ForeignChildren = extensions.ForeignChildren
                .Where(child => !BpmnGeneratedCondition.IsModelElement(child.Element.Name))
                .ToList()
        };
    }

    /// <summary>
    /// Whether the flow carries an author's <c>conditionExpression</c> as a retained foreign child, so the
    /// writer must not invent a second one. Only a payload serialized before format 1.3.0 has one there; a
    /// read now models it as <see cref="BpmnSequenceFlow.ConditionExpression"/>.
    /// </summary>
    private static bool HasAuthorConditionExpression(BpmnExtensions? extensions) =>
        extensions is not null
        && extensions.ForeignChildren.Any(child => BpmnGeneratedCondition.IsModelElement(child.Element.Name));

    /// <summary>Adds a child at the position its name takes in the canonical order, rather than at the end.</summary>
    private static void InsertByRank(XElement parent, XElement child)
    {
        var rank = BpmnChildOrder.RankOf(child.Name);
        var successor = parent.Elements().FirstOrDefault(existing => BpmnChildOrder.RankOf(existing.Name) > rank);
        if (successor is null) parent.Add(child);
        else successor.AddBeforeSelf(child);
    }

    /// <summary>
    /// Declares every namespace the retained content uses at the root, with a prefix guessed from the
    /// namespace's own host name, so <c>camunda:formData</c> stays readable rather than becoming
    /// <c>p7:formData</c>. XML-name prefixes may change; recorded QName-value bindings are restored locally
    /// and checked separately before the document is returned.
    /// </summary>
    private static void DeclareForeignNamespaces(XElement root, BpmnVendorNames vendor)
    {
        var namespaces = new SortedSet<string>(StringComparer.Ordinal);
        var unboundPrefixes = new HashSet<string>(StringComparer.Ordinal);
        var bindingsByElement = new Dictionary<XElement, Dictionary<string, string?>>(ReferenceEqualityComparer.Instance);

        foreach (var node in root.DescendantsAndSelf())
        {
            foreach (var attribute in node.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration))
            {
                var bindings = BpmnExtensionCapture.ValueNamespaceBindings(attribute);
                if (bindings is null) continue;

                if (!bindingsByElement.TryGetValue(node, out var elementBindings))
                    bindingsByElement.Add(node, elementBindings = new Dictionary<string, string?>(StringComparer.Ordinal));

                foreach (var binding in bindings)
                {
                    ValidateQNamePrefix(binding.Prefix);
                    if (binding.Namespace is null)
                        unboundPrefixes.Add(binding.Prefix);
                    else if (string.IsNullOrEmpty(binding.Namespace))
                        throw new BpmnInterchangeException($"Cannot preserve QName prefix '{binding.Prefix}' because its namespace is empty.");

                    if (elementBindings.TryGetValue(binding.Prefix, out var existing)
                        && !StringComparer.Ordinal.Equals(existing, binding.Namespace))
                        throw new BpmnInterchangeException($"Cannot preserve QName prefix '{binding.Prefix}' because one element uses it for conflicting namespaces.");

                    elementBindings[binding.Prefix] = binding.Namespace;
                }
            }
        }

        var fixedPrefixes = new HashSet<string>(StringComparer.Ordinal)
        {
            "bpmndi", "dc", "di", vendor.Prefix
        };
        foreach (var prefix in unboundPrefixes)
            if (fixedPrefixes.Contains(prefix))
                throw new BpmnInterchangeException($"Cannot preserve unbound QName prefix '{prefix}' because the writer must declare that prefix for BPMN output.");

        foreach (var element in root.DescendantsAndSelf())
        {
            if (!bindingsByElement.TryGetValue(element, out var bindings)) continue;
            foreach (var (prefix, namespaceName) in bindings.OrderBy(binding => binding.Key, StringComparer.Ordinal))
            {
                if (namespaceName is null) continue;
                if (prefix == "xmlns")
                    throw new BpmnInterchangeException("The reserved 'xmlns' prefix cannot be restored in a retained attribute value.");

                if (prefix == "xml")
                {
                    if (!StringComparer.Ordinal.Equals(namespaceName, XNamespace.Xml.NamespaceName))
                        throw new BpmnInterchangeException("The reserved 'xml' prefix cannot be rebound in a retained attribute value.");
                    continue;
                }

                var declarationName = XNamespace.Xmlns + prefix;
                var declaredHere = (string?)element.Attribute(declarationName);
                if (declaredHere is not null && !StringComparer.Ordinal.Equals(declaredHere, namespaceName))
                    throw new BpmnInterchangeException($"Cannot preserve QName prefix '{prefix}' because the writer already uses it for another namespace on the same element.");

                if (ReferenceEquals(element, root))
                {
                    var inScope = element.GetNamespaceOfPrefix(prefix)?.NamespaceName;
                    if (inScope is not null && !StringComparer.Ordinal.Equals(inScope, namespaceName))
                        throw new BpmnInterchangeException($"Cannot preserve QName prefix '{prefix}' because the document root already binds it to another namespace.");
                }

                if (declaredHere is null)
                    element.SetAttributeValue(declarationName, namespaceName);
            }
        }

        void Consider(XName name)
        {
            var ns = name.Namespace;
            if (ns == XNamespace.None || ns == vendor.Namespace || BpmnXmlNames.IsOwnedNamespace(ns)) return;
            namespaces.Add(ns.NamespaceName);
        }

        foreach (var node in root.DescendantsAndSelf())
        {
            Consider(node.Name);
            foreach (var attribute in node.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration))
                Consider(attribute.Name);
        }

        var taken = new HashSet<string>(fixedPrefixes, StringComparer.Ordinal) { "bpmn", "xml", "xmlns", "xsi" };
        foreach (var prefix in unboundPrefixes)
            taken.Add(prefix);
        foreach (var declaration in root.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
            if (declaration.Name.Namespace == XNamespace.Xmlns)
                taken.Add(declaration.Name.LocalName);

        var fallback = 1;
        foreach (var ns in namespaces)
        {
            if (root.GetPrefixOfNamespace(ns) is not null) continue;
            var prefix = SuggestPrefix(ns);
            if (prefix is null || !taken.Add(prefix))
            {
                do prefix = $"ext{fallback++}"; while (!taken.Add(prefix));
            }

            root.SetAttributeValue(XNamespace.Xmlns + prefix, ns);
        }

        foreach (var element in root.DescendantsAndSelf())
        {
            if (!bindingsByElement.TryGetValue(element, out var bindings)) continue;
            foreach (var (prefix, expectedNamespace) in bindings.OrderBy(binding => binding.Key, StringComparer.Ordinal))
            {
                var actualNamespace = element.GetNamespaceOfPrefix(prefix)?.NamespaceName;
                if (!StringComparer.Ordinal.Equals(actualNamespace, expectedNamespace))
                {
                    var expectation = expectedNamespace is null ? "unbound" : $"bound to '{expectedNamespace}'";
                    throw new BpmnInterchangeException($"Cannot preserve QName prefix '{prefix}' in a retained attribute value: expected it to be {expectation}, but the writer would bind it to '{actualNamespace ?? "(unbound)"}'.");
                }
            }
        }
    }

    private static void ValidateQNamePrefix(string prefix)
    {
        try
        {
            XmlConvert.VerifyNCName(prefix);
        }
        catch (XmlException exception)
        {
            throw new BpmnInterchangeException($"Cannot preserve invalid QName prefix '{prefix}' in a retained attribute value.", exception);
        }
    }

    /// <summary>
    /// Picks a readable prefix for a retained namespace. XML Schema instance is always <c>xsi</c> —
    /// the conventional prefix for <c>xsi:type</c> — rather than a host-derived guess such as <c>w3</c>.
    /// That changes output bytes, not meaning.
    /// </summary>
    private static string? SuggestPrefix(string ns)
    {
        if (StringComparer.Ordinal.Equals(ns, BpmnXmlNames.SchemaInstance.NamespaceName))
            return "xsi";
        if (!Uri.TryCreate(ns, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host)) return null;
        var label = uri.Host.Split('.').FirstOrDefault(part => part.Length > 0 && !StringComparer.OrdinalIgnoreCase.Equals(part, "www"));
        if (label is null || label.Length == 0 || !char.IsLetter(label[0])) return null;
        return new string(label.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant() is { Length: > 0 } prefix ? prefix : null;
    }

    private static BpmnEventDefinition? FindDefinition(BpmnElement element, string type) =>
        element.EventDefinitions.FirstOrDefault(definition => StringComparer.Ordinal.Equals(definition.Type, type));

    private static bool HasDefinition(BpmnElement element, string type) =>
        element.EventDefinitions.Any(definition => StringComparer.Ordinal.Equals(definition.Type, type));

    internal static string SanitizeId(string value)
    {
        var sanitized = new string(value.Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '-').ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "process" : sanitized;
    }

    // ---------------------------------------------------------------------------------------------------
    // Write-time state
    // ---------------------------------------------------------------------------------------------------

    /// <summary>A declaration the writer had to invent because the model referenced a name no root declaration covered.</summary>
    private readonly record struct SynthesizedDeclaration(string LocalName, string Id, string? Name, string? CodeAttribute, string? Code);

    private sealed class WriteContext
    {
        private readonly Dictionary<string, BpmnProcessDefinition> _nestedByBindingRef = new(StringComparer.Ordinal);
        private readonly Dictionary<string, BpmnWorkBinding> _bindingsByRef = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _messageIdByName = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _signalIdByName = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _escalationIdByCode = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _errorIdByCode = new(StringComparer.Ordinal);
        private readonly HashSet<string> _takenErrorIds = new(StringComparer.Ordinal);
        private readonly List<SynthesizedDeclaration> _synthesized = [];

        public WriteContext(BpmnDefinitions definitions, IReadOnlyList<BpmnWorkBinding>? bindings, BpmnVendorNames vendor)
        {
            Vendor = vendor;
            ExpressionLanguage = DocumentExpressionLanguage(definitions);

            foreach (var binding in bindings ?? [])
            {
                _bindingsByRef[binding.BindingRef] = binding;
                if (binding is BpmnWorkBinding.NestedProcess nested)
                    _nestedByBindingRef[nested.BindingRef] = nested.Definition;
            }

            foreach (var message in definitions.Messages.Where(entry => !string.IsNullOrWhiteSpace(entry.Name)))
                _messageIdByName.TryAdd(message.Name!.Trim(), message.Id);
            foreach (var signal in definitions.Signals.Where(entry => !string.IsNullOrWhiteSpace(entry.Name)))
                _signalIdByName.TryAdd(signal.Name!.Trim(), signal.Id);
            foreach (var escalation in definitions.Escalations)
            {
                var code = escalation.EscalationCode ?? escalation.Name ?? escalation.Id;
                _escalationIdByCode.TryAdd(code.Trim(), escalation.Id);
            }

            // An <error> without an errorCode is matched on its id, so the id is its code here too.
            foreach (var error in definitions.Errors)
                _errorIdByCode.TryAdd((error.ErrorCode ?? error.Id).Trim(), error.Id);

            // A generated error id has to step around every id the document already gives a declaration, and
            // around each recorded errorRef, which keeps its own id whenever the loop below reaches it.
            _takenErrorIds.UnionWith(definitions.Messages.Select(message => message.Id));
            _takenErrorIds.UnionWith(definitions.Signals.Select(signal => signal.Id));
            _takenErrorIds.UnionWith(definitions.Escalations.Select(escalation => escalation.Id));
            _takenErrorIds.UnionWith(definitions.Errors.Select(error => error.Id));
            foreach (var process in AllProcesses(definitions))
                foreach (var element in process.Elements)
                    if (element.Properties.TryGetValue(BpmnXmlReader.ErrorRefPropertyKey, out var recordedRef) && !string.IsNullOrWhiteSpace(recordedRef))
                        _takenErrorIds.Add(recordedRef.Trim());

            var declaredErrorIds = definitions.Errors.Select(error => error.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var process in AllProcesses(definitions))
                foreach (var element in process.Elements)
                {
                    foreach (var definition in element.EventDefinitions)
                    {
                        if (definition.Properties.TryGetValue(BpmnEventDefinitionProperties.Name, out var name) && !string.IsNullOrWhiteSpace(name))
                        {
                            if (StringComparer.Ordinal.Equals(definition.Type, BpmnEventDefinitionTypes.Message)) EnsureMessage(name.Trim());
                            else if (StringComparer.Ordinal.Equals(definition.Type, BpmnEventDefinitionTypes.Signal)) EnsureSignal(name.Trim());
                        }

                        if (StringComparer.Ordinal.Equals(definition.Type, BpmnEventDefinitionTypes.Escalation)
                            && definition.Properties.TryGetValue(BpmnEventDefinitionProperties.Code, out var code) && !string.IsNullOrWhiteSpace(code))
                            EnsureEscalation(code.Trim(), definition.Properties.TryGetValue(BpmnEventDefinitionProperties.Name, out var display) ? display : null);
                    }

                    if (element.Properties.TryGetValue(BpmnXmlReader.MessageNamePropertyKey, out var taskMessage) && !string.IsNullOrWhiteSpace(taskMessage))
                        EnsureMessage(taskMessage.Trim());

                    if (element.EventDefinitions.FirstOrDefault(definition => StringComparer.Ordinal.Equals(definition.Type, BpmnEventDefinitionTypes.Error)) is { } errorDefinition)
                        DeclareError(element, errorDefinition, declaredErrorIds);
                }

            foreach (var flow in definitions.Collaboration?.MessageFlows ?? [])
                if (!string.IsNullOrWhiteSpace(flow.MessageName))
                    EnsureMessage(flow.MessageName!.Trim());
        }

        /// <summary>The vendor namespace this write emits the library's own non-standard names in.</summary>
        public BpmnVendorNames Vendor { get; }

        /// <summary>The <c>expressionLanguage</c> this write declares on <c>&lt;definitions&gt;</c>, if any.</summary>
        public string? ExpressionLanguage { get; }

        /// <summary>
        /// The document's own value, else the one default every process agrees on. The second case is a
        /// process written on its own, or processes built in code into a fresh document. When processes
        /// disagree, none is declared; each process's expressions then carry their language themselves.
        /// </summary>
        private static string? DocumentExpressionLanguage(BpmnDefinitions definitions)
        {
            if (!string.IsNullOrWhiteSpace(definitions.ExpressionLanguage))
                return definitions.ExpressionLanguage.Trim();

            var declared = definitions.Processes
                .Select(process => process.ExpressionLanguage?.Trim())
                .Where(language => !string.IsNullOrEmpty(language))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            return declared.Length == 1 ? declared[0] : null;
        }

        public IReadOnlyList<SynthesizedDeclaration> SynthesizedDeclarations => _synthesized;

        public BpmnProcessDefinition? NestedProcess(string bindingRef) =>
            _nestedByBindingRef.TryGetValue(bindingRef, out var nested) ? nested : null;

        public BpmnWorkBinding.CallProcess? CallBinding(string bindingRef) => Binding(bindingRef) as BpmnWorkBinding.CallProcess;

        /// <summary>The work an element binds, through either of its two binding channels.</summary>
        public BpmnWorkBinding? WorkFor(BpmnElement element) => Binding(element.BindingRef) ?? Binding(element.ListenerBindingRef);

        /// <summary>The duration an element's timer binding carries, for a model that put it there rather than on the event definition.</summary>
        public string? TimerDurationFor(BpmnElement element) =>
            WorkFor(element) is BpmnWorkBinding.TimerWait timer && !string.IsNullOrWhiteSpace(timer.IsoDuration) ? timer.IsoDuration.Trim() : null;

        /// <summary>The cycle an element's timer binding carries, for a model that put it there rather than on the event definition.</summary>
        public string? TimerCycleFor(BpmnElement element) =>
            WorkFor(element) is BpmnWorkBinding.TimerCycleWait timer && !string.IsNullOrWhiteSpace(timer.Cycle) ? timer.Cycle.Trim() : null;

        /// <summary>The instant an element's timer binding carries, for a model that put it there rather than on the event definition.</summary>
        public string? TimerDateFor(BpmnElement element) =>
            WorkFor(element) is BpmnWorkBinding.TimerDateWait timer && !string.IsNullOrWhiteSpace(timer.IsoDate) ? timer.IsoDate.Trim() : null;

        /// <summary>The message or signal name an element's binding carries, for a model that put it there rather than on the event definition.</summary>
        public string? EventNameFor(BpmnElement element, bool isMessage) => WorkFor(element) switch
        {
            BpmnWorkBinding.MessageWait wait when isMessage => Trimmed(wait.MessageName),
            BpmnWorkBinding.MessagePublish publish when isMessage => Trimmed(publish.MessageName),
            BpmnWorkBinding.SignalWait signal when !isMessage => Trimmed(signal.SignalName),
            BpmnWorkBinding.SignalPublish broadcast when !isMessage => Trimmed(broadcast.SignalName),
            _ => null
        };

        private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private BpmnWorkBinding? Binding(string? bindingRef) =>
            bindingRef is not null && _bindingsByRef.TryGetValue(bindingRef, out var binding) ? binding : null;

        public string MessageDeclarationId(string name) => EnsureMessage(name.Trim());
        public string SignalDeclarationId(string name) => EnsureSignal(name.Trim());
        public string EscalationDeclarationId(string code) => EnsureEscalation(code.Trim(), null);
        public string ErrorDeclarationId(string code) => EnsureError(code.Trim(), null);

        /// <summary>Every process in the document, top-level first and each subprocess body after its parent.</summary>
        public IEnumerable<BpmnProcessDefinition> AllProcesses(BpmnDefinitions definitions)
        {
            foreach (var process in definitions.Processes)
                foreach (var nested in Walk(process))
                    yield return nested;
        }

        private IEnumerable<BpmnProcessDefinition> Walk(BpmnProcessDefinition process)
        {
            yield return process;
            foreach (var element in process.Elements)
            {
                if (element.BindingRef is not { } bindingRef || !_nestedByBindingRef.TryGetValue(bindingRef, out var nested))
                    continue;
                foreach (var deeper in Walk(nested))
                    yield return deeper;
            }
        }

        private string EnsureMessage(string name)
        {
            if (_messageIdByName.TryGetValue(name, out var id)) return id;
            id = $"message-{SanitizeId(name)}";
            _messageIdByName[name] = id;
            _synthesized.Add(new SynthesizedDeclaration("message", id, name, null, null));
            return id;
        }

        private string EnsureSignal(string name)
        {
            if (_signalIdByName.TryGetValue(name, out var id)) return id;
            id = $"signal-{SanitizeId(name)}";
            _signalIdByName[name] = id;
            _synthesized.Add(new SynthesizedDeclaration("signal", id, name, null, null));
            return id;
        }

        /// <summary>
        /// Makes sure the <c>&lt;error&gt;</c> an error event definition will point at exists. A recorded
        /// <c>errorRef</c> naming no declaration gets one with that id, carrying the definition's code when it
        /// differs from the id (an id is its own code). A definition with only a code gets the declaration for
        /// that code.
        /// </summary>
        private void DeclareError(BpmnElement element, BpmnEventDefinition definition, HashSet<string> declaredErrorIds)
        {
            var code = definition.Properties.TryGetValue(BpmnEventDefinitionProperties.Code, out var codeValue) && !string.IsNullOrWhiteSpace(codeValue) ? codeValue.Trim() : null;
            var name = definition.Properties.TryGetValue(BpmnEventDefinitionProperties.Name, out var nameValue) && !string.IsNullOrWhiteSpace(nameValue) ? nameValue.Trim() : null;
            if (element.Properties.TryGetValue(BpmnXmlReader.ErrorRefPropertyKey, out var errorRef) && !string.IsNullOrWhiteSpace(errorRef))
            {
                var id = errorRef.Trim();
                if (declaredErrorIds.Add(id))
                {
                    var declaredCode = code is not null && !StringComparer.Ordinal.Equals(code, id) ? code : null;
                    _synthesized.Add(new SynthesizedDeclaration("error", id, name, "errorCode", declaredCode));
                    _errorIdByCode.TryAdd(code ?? id, id);
                }
                return;
            }

            if (code is not null)
                EnsureError(code, name);
        }

        /// <summary>
        /// The declaration for <paramref name="code"/>, synthesized when none exists. Sanitizing is lossy, so two
        /// codes such as <c>A.B</c> and <c>A/B</c> share a stem; a numeric suffix keeps the second id distinct,
        /// and the order the model is walked in decides which gets it, so the same model writes the same ids.
        /// </summary>
        private string EnsureError(string code, string? name)
        {
            if (_errorIdByCode.TryGetValue(code, out var id)) return id;
            var stem = $"error-{SanitizeId(code)}";
            id = stem;
            for (var suffix = 2; !_takenErrorIds.Add(id); suffix++)
                id = $"{stem}-{suffix}";
            _errorIdByCode[code] = id;
            _synthesized.Add(new SynthesizedDeclaration("error", id, string.IsNullOrWhiteSpace(name) ? null : name.Trim(), "errorCode", code));
            return id;
        }

        private string EnsureEscalation(string code, string? name)
        {
            if (_escalationIdByCode.TryGetValue(code, out var id)) return id;
            id = $"escalation-{SanitizeId(code)}";
            _escalationIdByCode[code] = id;
            _synthesized.Add(new SynthesizedDeclaration("escalation", id, string.IsNullOrWhiteSpace(name) ? null : name.Trim(), "escalationCode", code));
            return id;
        }

    }

    /// <summary>
    /// Works out where everything is. Retained shapes win; anything the document did not lay out is placed on
    /// a left-to-right row per process, with subprocess bodies laid out inside their parent box and boundary
    /// events pinned to the lower edge of their host.
    /// </summary>
    private sealed class DiagramLayout
    {
        private readonly Dictionary<string, BpmnBounds> _bounds = new(StringComparer.Ordinal);
        private readonly Dictionary<string, BpmnShape> _retainedShapes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _canonicalOrder = new(StringComparer.Ordinal);
        private readonly List<BpmnShape> _shapes = [];
        private readonly List<BpmnEdge> _edges = [];

        public DiagramLayout(BpmnDefinitions definitions, WriteContext context)
        {
            BuildCanonicalOrder(definitions, context);

            foreach (var shape in definitions.Diagrams.SelectMany(diagram => diagram.Plane.Shapes))
            {
                _retainedShapes.TryAdd(shape.BpmnElementRef, shape);
                _bounds.TryAdd(shape.BpmnElementRef, shape.Bounds);
            }

            var top = 80d;
            var poolsByProcess = (definitions.Collaboration?.Pools ?? [])
                .Where(pool => pool.ProcessRef is not null)
                .GroupBy(pool => pool.ProcessRef!, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

            foreach (var process in definitions.Processes)
            {
                var hasPool = poolsByProcess.ContainsKey(process.ProcessId);
                var box = LayoutContainer(process, context, hasPool ? PoolHeaderWidth + ContainerPadding : ContainerPadding, top);

                if (poolsByProcess.TryGetValue(process.ProcessId, out var pool))
                {
                    var poolBounds = Pad(box, PoolHeaderWidth + ContainerPadding, ContainerPadding);
                    Place(pool.PoolId, poolBounds, isHorizontal: true);
                    LayoutLanes(process, poolBounds);
                    box = poolBounds;
                }

                top = box.Y + box.Height + RowGap;
            }

            // A black-box pool has no process to size it, so it gets a plain band of its own.
            foreach (var pool in (definitions.Collaboration?.Pools ?? []).Where(pool => pool.ProcessRef is null))
            {
                Place(pool.PoolId, new BpmnBounds(ContainerPadding, top, 600, 120), isHorizontal: true);
                top += 120 + RowGap;
            }

            foreach (var process in context.AllProcesses(definitions))
                foreach (var flow in process.SequenceFlows)
                    _edges.Add(new BpmnEdge(null, flow.FlowId, Route(BoundsOf(flow.SourceRef), BoundsOf(flow.TargetRef))));

            foreach (var flow in definitions.Collaboration?.MessageFlows ?? [])
            {
                var sourceRef = flow.SourceElementId ?? flow.SourcePoolId;
                var targetRef = flow.TargetElementId ?? flow.TargetPoolId;
                if (string.IsNullOrWhiteSpace(sourceRef) || string.IsNullOrWhiteSpace(targetRef)) continue;
                _edges.Add(new BpmnEdge(null, flow.FlowId, Route(BoundsOf(sourceRef), BoundsOf(targetRef))));
            }
        }

        public IEnumerable<BpmnShape> AllShapes() => _shapes;

        public IEnumerable<BpmnEdge> AllEdges() => _edges;

        /// <summary>
        /// Orders diagram entries by the position of the thing they draw, so the output does not depend on
        /// which generation produced the input. Anything the model does not know about sorts last, by id, so
        /// even unrecognized entries land somewhere stable.
        /// </summary>
        public IEnumerable<T> InCanonicalOrder<T>(IEnumerable<T> items, Func<T, string> reference) =>
            items
                .OrderBy(item => _canonicalOrder.TryGetValue(reference(item), out var index) ? index : int.MaxValue)
                .ThenBy(reference, StringComparer.Ordinal);

        /// <summary>
        /// The order the model itself implies: pools, then the lanes inside them, then flow elements in
        /// document order with each subprocess body following its parent, then sequence flows, then message
        /// flows. Containers precede the things they contain, which is also the order a renderer wants.
        /// </summary>
        private void BuildCanonicalOrder(BpmnDefinitions definitions, WriteContext context)
        {
            var next = 0;

            void Add(string reference)
            {
                if (!_canonicalOrder.ContainsKey(reference)) _canonicalOrder[reference] = next++;
            }

            foreach (var pool in definitions.Collaboration?.Pools ?? [])
                Add(pool.PoolId);
            foreach (var lane in definitions.Processes.SelectMany(process => process.Lanes))
                Add(lane.LaneId);

            var processes = context.AllProcesses(definitions).ToArray();
            foreach (var element in processes.SelectMany(process => process.Elements))
                Add(element.ElementId);
            foreach (var flow in processes.SelectMany(process => process.SequenceFlows))
                Add(flow.FlowId);
            foreach (var flow in definitions.Collaboration?.MessageFlows ?? [])
                Add(flow.FlowId);
        }

        /// <summary>
        /// BPMN DI requires an edge to have at least two waypoints, and modelers reject one that does not, so
        /// an edge that arrived with fewer gets a synthesized route.
        /// </summary>
        public BpmnEdge Repair(BpmnEdge edge) =>
            edge.Waypoints.Count >= 2
                ? edge
                : edge with { Waypoints = _edges.FirstOrDefault(candidate => StringComparer.Ordinal.Equals(candidate.BpmnElementRef, edge.BpmnElementRef))?.Waypoints ?? Route(default, default) };

        private BpmnBounds LayoutContainer(BpmnProcessDefinition process, WriteContext context, double originX, double originY)
        {
            var flowNodes = process.Elements.Where(element => !StringComparer.Ordinal.Equals(element.ElementType, BpmnElementTypes.BoundaryEvent)).ToArray();
            var x = originX;
            var right = originX;
            var bottom = originY + BpmnLayoutDefaults.TaskHeight;

            // One pass, left to right. A subprocess body is laid out at the position its own box will take, so
            // the parent always encloses its children rather than being drawn somewhere else on the plane.
            foreach (var element in flowNodes)
            {
                BpmnBounds placed;
                if (_bounds.TryGetValue(element.ElementId, out var retained))
                {
                    placed = retained;
                }
                else if (element.BindingRef is { } bindingRef && context.NestedProcess(bindingRef) is { Elements.Count: > 0 } nested)
                {
                    var inner = LayoutContainer(nested, context, x + ContainerPadding, originY + ContainerPadding);
                    placed = Pad(inner, ContainerPadding, ContainerPadding);
                    Place(element.ElementId, placed, isExpanded: true);
                }
                else
                {
                    var (width, height) = SizeFor(element);
                    placed = new BpmnBounds(x, originY + (BpmnLayoutDefaults.TaskHeight - height) / 2, width, height);
                    Place(element.ElementId, placed,
                        isMarkerVisible: StringComparer.Ordinal.Equals(element.ElementType, BpmnElementTypes.ExclusiveGateway) ? true : null);
                }

                x = Math.Max(x, placed.X + placed.Width) + ElementGap;
                right = Math.Max(right, placed.X + placed.Width);
                bottom = Math.Max(bottom, placed.Y + placed.Height);
            }

            // Boundary events sit on the lower edge of the activity they interrupt.
            foreach (var boundary in process.Elements.Where(element => StringComparer.Ordinal.Equals(element.ElementType, BpmnElementTypes.BoundaryEvent)))
            {
                if (_bounds.ContainsKey(boundary.ElementId)) continue;
                var host = boundary.AttachedToRef is { } attachedTo && _bounds.TryGetValue(attachedTo, out var hostBounds)
                    ? hostBounds
                    : new BpmnBounds(x, originY, BpmnLayoutDefaults.TaskWidth, BpmnLayoutDefaults.TaskHeight);
                var size = BpmnLayoutDefaults.EventSize;
                Place(boundary.ElementId, new BpmnBounds(host.X + host.Width - size * 1.5, host.Y + host.Height - size / 2, size, size));
            }

            return new BpmnBounds(originX, originY, Math.Max(right - originX, BpmnLayoutDefaults.TaskWidth), Math.Max(bottom - originY, BpmnLayoutDefaults.TaskHeight));
        }

        private void LayoutLanes(BpmnProcessDefinition process, BpmnBounds poolBounds)
        {
            if (process.Lanes.Count == 0) return;
            var laneHeight = poolBounds.Height / process.Lanes.Count;
            for (var index = 0; index < process.Lanes.Count; index++)
            {
                var lane = process.Lanes[index];
                if (_bounds.ContainsKey(lane.LaneId)) continue;
                Place(lane.LaneId,
                    new BpmnBounds(poolBounds.X + PoolHeaderWidth, poolBounds.Y + index * laneHeight, poolBounds.Width - PoolHeaderWidth, laneHeight),
                    isHorizontal: true);
            }
        }

        private void Place(string reference, BpmnBounds bounds, bool? isHorizontal = null, bool? isExpanded = null, bool? isMarkerVisible = null)
        {
            _bounds[reference] = bounds;
            if (_retainedShapes.ContainsKey(reference)) return;
            _shapes.Add(new BpmnShape($"{SanitizeId(reference)}_di", reference, bounds, isHorizontal, isExpanded, isMarkerVisible));
        }

        private BpmnBounds BoundsOf(string reference) =>
            _bounds.TryGetValue(reference, out var bounds) ? bounds : new BpmnBounds(0, 0, BpmnLayoutDefaults.TaskWidth, BpmnLayoutDefaults.TaskHeight);

        private static BpmnBounds Pad(BpmnBounds inner, double horizontal, double vertical) =>
            new(inner.X - horizontal, inner.Y - vertical, inner.Width + horizontal * 2, inner.Height + vertical * 2);

        private static (double Width, double Height) SizeFor(BpmnElement element)
        {
            if (element.ElementType.EndsWith("Gateway", StringComparison.Ordinal))
                return (BpmnLayoutDefaults.GatewaySize, BpmnLayoutDefaults.GatewaySize);
            if (element.ElementType is BpmnElementTypes.StartEvent or BpmnElementTypes.EndEvent
                or BpmnElementTypes.IntermediateCatchEvent or BpmnElementTypes.IntermediateThrowEvent
                or BpmnElementTypes.BoundaryEvent)
                return (BpmnLayoutDefaults.EventSize, BpmnLayoutDefaults.EventSize);
            return (BpmnLayoutDefaults.TaskWidth, BpmnLayoutDefaults.TaskHeight);
        }

        /// <summary>
        /// A straight two-point route from the source's right edge to the target's left edge, or, when the
        /// flow runs backwards, a three-point elbow that dips below both boxes so loop-back edges in a cyclic
        /// graph do not lie on top of the forward ones.
        /// </summary>
        private static IReadOnlyList<BpmnPoint> Route(BpmnBounds source, BpmnBounds target)
        {
            if (source.X > target.X)
            {
                var drop = Math.Max(source.Y + source.Height, target.Y + target.Height) + LoopBackDrop;
                var sourceCentre = source.X + source.Width / 2;
                var targetCentre = target.X + target.Width / 2;
                return
                [
                    new BpmnPoint(sourceCentre, source.Y + source.Height),
                    new BpmnPoint((sourceCentre + targetCentre) / 2, drop),
                    new BpmnPoint(targetCentre, target.Y + target.Height)
                ];
            }

            return
            [
                new BpmnPoint(source.X + source.Width, source.Y + source.Height / 2),
                new BpmnPoint(target.X, target.Y + target.Height / 2)
            ];
        }
    }
}
