using System.Globalization;
using System.Xml.Linq;
using Bpmn.Model;

namespace Bpmn.Interchange;

/// <summary>
/// Reads BPMN 2.0 XML into the neutral <see cref="BpmnDefinitions"/> object model.
/// <para>
/// The reader never invents an implementation. Where an element needs work performed - a task, a call
/// activity, a subprocess, an event a token waits on - it records a <see cref="BpmnWorkBinding"/> saying what
/// kind of work in BPMN terms, and stamps the matching <see cref="BpmnElement.BindingRef"/> on the element.
/// Choosing and running the implementation is the host's job.
/// </para>
/// <para>
/// <see cref="Analyze"/> and <see cref="Read"/> run the same code, so a dry run can never disagree with the
/// real one about what a document costs. Anything that cannot be represented is reported as a
/// <see cref="BpmnImportIssue"/> on an Info / Degraded / Dropped ladder rather than thrown; only a document
/// that is not readable at all raises <see cref="BpmnInterchangeException"/>.
/// </para>
/// <para>
/// At the default <see cref="BpmnFidelity.Lossless"/> fidelity, documentation, vendor extension elements,
/// foreign attributes, and unrecognized children are retained so a read-modify-write cycle does not quietly
/// strip a modeler's annotations.
/// </para>
/// </summary>
public sealed class BpmnXmlReader
{
    /// <summary>The element property key carrying a call activity's BPMN <c>calledElement</c>, kept for round-trip.</summary>
    public const string CalledElementPropertyKey = "bpmn.calledElement";

    /// <summary>
    /// The element property key carrying a send/receive task's resolved message name. A task references its
    /// message through a <c>messageRef</c> attribute rather than a nested event definition, so the name is
    /// recorded here for the root <c>&lt;message&gt;</c> declaration and for round-trip.
    /// </summary>
    public const string MessageNamePropertyKey = "bpmn.messageName";

    /// <summary>The element property key carrying an error event definition's <c>errorRef</c>, kept for round-trip and error-code matching.</summary>
    public const string ErrorRefPropertyKey = "bpmn.errorRef";

    /// <summary>The default prefix for generated binding refs.</summary>
    public const string DefaultBindingRefPrefix = "node";

    /// <summary>
    /// Reports what a document contains and what a read would cost, without producing a model. Runs exactly
    /// the code <see cref="Read"/> runs.
    /// </summary>
    public BpmnImportAnalysis Analyze(string xml, BpmnImportOptions? options = null)
    {
        var context = new ReadContext(options);
        ReadCore(xml, options, context);
        return context.ToAnalysis();
    }

    /// <summary>Reads a document into the neutral model, the work bindings its elements need, and the analysis.</summary>
    public BpmnImportResult Read(string xml, BpmnImportOptions? options = null)
    {
        var context = new ReadContext(options);
        var definitions = ReadCore(xml, options, context);
        return new BpmnImportResult(definitions, context.Bindings.ToArray(), context.ToAnalysis());
    }

    private static BpmnDefinitions ReadCore(string xml, BpmnImportOptions? options, ReadContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xml);

        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException exception)
        {
            throw new BpmnInterchangeException("The document is not well-formed XML.", exception);
        }

        var root = document.Root;
        if (root is null || root.Name != BpmnXmlNames.Model + "definitions")
            throw new BpmnInterchangeException("The document root is not a BPMN 2.0 <definitions> element.");

        var processElements = root.Elements(BpmnXmlNames.Model + "process").ToArray();
        foreach (var candidate in processElements)
            context.ProcessIds.Add(IdOf(candidate) ?? "(no id)");
        if (processElements.Length == 0)
            throw new BpmnInterchangeException("The document contains no <process> element.");

        // A caller that names a process is telling us what it cares about; naming one the document does not
        // declare is a mistake worth failing on rather than quietly reading something else.
        if (options?.ProcessId is { } requested
            && !processElements.Any(candidate => StringComparer.Ordinal.Equals(IdOf(candidate), requested)))
            throw new BpmnInterchangeException($"The document contains no process with id '{requested}'.");

        // Document-level indexes are read once and shared by every process: the root message, signal, error,
        // and escalation catalogs plus the diagram and collaboration content do not vary per process.
        var messages = ReadNamedDeclarations(root, "message").Select(entry => new BpmnMessageDeclaration(entry.Key, entry.Value)).ToArray();
        var signals = ReadNamedDeclarations(root, "signal").Select(entry => new BpmnSignalDeclaration(entry.Key, entry.Value)).ToArray();
        var errors = ReadErrorDeclarations(root);
        var escalationDeclarations = ReadEscalationDeclarations(root);
        var escalations = escalationDeclarations
            .Select(entry => new BpmnEscalationDeclaration(entry.Key, entry.Value.Name, entry.Value.Code))
            .ToArray();
        var messageSignalNames = ReadMessageSignalNames(root);
        var participants = ReadParticipants(root);
        var messageFlowDeclarations = ReadMessageFlows(root);
        var diagrams = ReadDiagrams(root);

        var built = new List<BuiltProcess>(processElements.Length);
        foreach (var processElement in processElements)
        {
            var processId = IdOf(processElement) ?? "process";
            var isExecutable = string.Equals((string?)processElement.Attribute("isExecutable"), "true", StringComparison.OrdinalIgnoreCase);
            var definition = BuildProcess(processElement, processId, isExecutable, messageSignalNames, escalationDeclarations, context);
            if (!isExecutable)
                context.Report(BpmnImportIssueSeverity.Info, $"Process '{processId}' is not executable (isExecutable=\"false\"); it read as a documentation-only pool.", processId: processId);
            built.Add(new BuiltProcess(processId, definition, isExecutable));
        }

        if (built.Count > 1)
            context.Report(BpmnImportIssueSeverity.Info, $"Document declares {built.Count} processes: {string.Join(", ", built.Select(entry => $"'{entry.ProcessId}'"))}.");

        var collaboration = ResolveCollaboration(root, built, participants, messageFlowDeclarations, messageSignalNames, context);

        foreach (var issue in context.Retention.ToIssues())
            context.Issues.Add(issue);

        return new BpmnDefinitions(
            IdOf(root),
            ((string?)root.Attribute("targetNamespace"))?.Trim(),
            ((string?)root.Attribute("exporter"))?.Trim(),
            ((string?)root.Attribute("exporterVersion"))?.Trim(),
            built.Select(entry => entry.Definition).ToArray(),
            collaboration,
            diagrams,
            messages,
            signals,
            errors,
            escalations,
            BpmnExtensionCapture.Capture(root, context.Fidelity, IsDefinitionsChildConsumed, context.Retention));
    }

    // ---------------------------------------------------------------------------------------------------
    // Collaboration
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Folds the document's <c>&lt;collaboration&gt;</c> onto the processes already read. Each
    /// <c>&lt;participant&gt;</c> becomes a pool (lanes gain the pool id when exactly one participant
    /// references their process); a participant with no <c>processRef</c> is a black-box pool; each
    /// <c>&lt;messageFlow&gt;</c> resolves both endpoints across every process, or against a black-box pool,
    /// and is recorded with a finding describing whether the wiring can actually carry a message.
    /// </summary>
    private static BpmnCollaboration? ResolveCollaboration(
        XElement root,
        IReadOnlyList<BuiltProcess> built,
        IReadOnlyList<ParticipantDeclaration> participants,
        IReadOnlyList<MessageFlowDeclaration> messageFlows,
        IReadOnlyDictionary<string, string> messageSignalNames,
        ReadContext context)
    {
        var collaborationElement = root.Element(BpmnXmlNames.Model + "collaboration");
        var byProcessId = new Dictionary<string, BuiltProcess>(StringComparer.Ordinal);
        foreach (var entry in built)
            byProcessId.TryAdd(entry.ProcessId, entry);
        var participantIds = participants.Select(participant => participant.Id).ToHashSet(StringComparer.Ordinal);

        var pools = new List<BpmnPool>();
        foreach (var participant in participants)
        {
            if (participant.ProcessRef is null)
            {
                context.Report(BpmnImportIssueSeverity.Info, $"Participant '{participant.Id}'{Named(participant.Name)} declares no processRef; it read as a black-box pool with no process.", participant.Id);
                pools.Add(new BpmnPool(participant.Id, participant.Name, processRef: null, isExecutable: false));
                continue;
            }

            if (!byProcessId.TryGetValue(participant.ProcessRef, out var target))
            {
                context.Report(BpmnImportIssueSeverity.Degraded, $"Participant '{participant.Id}'{Named(participant.Name)} references process '{participant.ProcessRef}', which is not a process in this document; its pool was not read.", participant.Id);
                continue;
            }

            pools.Add(new BpmnPool(participant.Id, participant.Name, participant.ProcessRef, target.IsExecutable));
            target.ReferencingParticipants.Add(participant);
        }

        // A pool id on a lane is only meaningful when one participant owns the process; when several do, the
        // lane belongs to no single pool and the ids stay unset.
        foreach (var entry in built)
        {
            if (entry.ReferencingParticipants.Count == 1)
                entry.LanePoolId = entry.ReferencingParticipants[0].Id;
            else if (entry.ReferencingParticipants.Count > 1 && entry.Definition.Lanes.Count > 0)
                context.Report(BpmnImportIssueSeverity.Info, $"Process '{entry.ProcessId}' is referenced by {entry.ReferencingParticipants.Count} participants; its lanes' pool ids were left unset (ambiguous).", processId: entry.ProcessId);

            if (entry.LanePoolId is { } poolId && entry.Definition.Lanes.Count > 0)
                entry.Definition = entry.Definition with
                {
                    Lanes = entry.Definition.Lanes.Select(lane => new BpmnLane(lane.LaneId, poolId, lane.Name)).ToArray()
                };
        }

        var elementIndex = new Dictionary<string, (BuiltProcess Owner, string? MessageName)>(StringComparer.Ordinal);
        foreach (var entry in built)
            foreach (var element in entry.Definition.Elements)
                elementIndex.TryAdd(element.ElementId, (entry, MessageNameOf(element)));

        MessageFlowEndpoint Resolve(string reference)
        {
            if (string.IsNullOrWhiteSpace(reference))
                return MessageFlowEndpoint.Unresolvable;
            if (elementIndex.TryGetValue(reference, out var hit))
                return MessageFlowEndpoint.Element(reference, hit.Owner.LanePoolId, hit.MessageName);
            if (participantIds.Contains(reference))
                return MessageFlowEndpoint.BlackBox(reference);
            return MessageFlowEndpoint.Unresolvable;
        }

        var resolvedFlows = new List<BpmnMessageFlow>();
        foreach (var flow in messageFlows)
        {
            var source = Resolve(flow.SourceRef);
            var target = Resolve(flow.TargetRef);

            if (source.Kind == MessageFlowEndpointKind.Unresolvable || target.Kind == MessageFlowEndpointKind.Unresolvable)
            {
                var unresolved = source.Kind == MessageFlowEndpointKind.Unresolvable ? flow.SourceRef : flow.TargetRef;
                context.Report(BpmnImportIssueSeverity.Degraded, $"Message flow '{flow.FlowId}' references '{(string.IsNullOrWhiteSpace(unresolved) ? "(missing)" : unresolved)}', which resolves to no element or pool in this document; it was recorded nowhere.", flow.FlowId);
                continue;
            }

            var declaredName = flow.MessageRef is { } messageRef && messageSignalNames.TryGetValue(messageRef, out var declared) && !string.IsNullOrWhiteSpace(declared)
                ? declared.Trim()
                : null;
            var messageName = declaredName ?? source.MessageName ?? target.MessageName;
            resolvedFlows.Add(new BpmnMessageFlow(flow.FlowId, flow.Name, source.ElementId, source.PoolId, target.ElementId, target.PoolId, messageName));

            if (source.Kind == MessageFlowEndpointKind.BlackBox || target.Kind == MessageFlowEndpointKind.BlackBox)
            {
                context.Report(BpmnImportIssueSeverity.Info, $"Message flow '{flow.FlowId}' has a black-box pool endpoint; it read as documentation only.", flow.FlowId);
                continue;
            }

            // Both endpoints are elements. Messages are matched by name, so a send and a receive whose names
            // differ - or where either side names no message - can never meet. That is a finding, not a
            // silent loss of wiring; the resolved flow is recorded either way.
            if (source.MessageName is { } sourceName && target.MessageName is { } targetName && StringComparer.Ordinal.Equals(sourceName, targetName))
                context.Report(BpmnImportIssueSeverity.Info, $"Message flow '{flow.FlowId}' wires '{source.ElementId}' to '{target.ElementId}' on message '{sourceName}'.", flow.FlowId);
            else
                context.Report(BpmnImportIssueSeverity.Degraded, $"Message flow '{flow.FlowId}' wires '{source.ElementId}' (message {Quote(source.MessageName)}) to '{target.ElementId}' (message {Quote(target.MessageName)}); the send and receive message names differ, so no message can pass between them.", flow.FlowId);
        }

        if (collaborationElement is null && pools.Count == 0 && resolvedFlows.Count == 0)
            return null;

        var collaborationExtensions = collaborationElement is null
            ? BpmnExtensions.Empty
            : BpmnExtensionCapture.Capture(collaborationElement, context.Fidelity, IsCollaborationChildConsumed, context.Retention);

        return new BpmnCollaboration(collaborationElement is null ? null : IdOf(collaborationElement), pools, resolvedFlows, collaborationExtensions);
    }

    // ---------------------------------------------------------------------------------------------------
    // Process content
    // ---------------------------------------------------------------------------------------------------

    private static BpmnProcessDefinition BuildProcess(
        XElement container,
        string processId,
        bool isExecutable,
        IReadOnlyDictionary<string, string> messageSignalNames,
        IReadOnlyDictionary<string, EscalationDeclaration> escalationDeclarations,
        ReadContext context,
        bool isTransaction = false,
        bool isEventSubprocessBody = false)
    {
        var previousProcessId = context.CurrentProcessId;
        context.CurrentProcessId = processId;

        var elements = new List<BpmnElement>();
        var flows = new List<BpmnSequenceFlow>();
        var lanes = new List<BpmnLane>();
        var pendingBoundaries = new List<XElement>();
        // Compensate throws, compensate ends, and boundary-to-handler associations resolve in later passes so
        // their targets are known regardless of the order the document happens to declare them in.
        var pendingCompensateThrows = new List<XElement>();
        var pendingCompensateEnds = new List<XElement>();
        var associations = new List<(string Source, string Target)>();
        // Retained per-element content is collected as each child is visited and stamped onto the elements in
        // one pass at the end, alongside the lane ids, because the element type is immutable.
        var retainedByElementId = new Dictionary<string, BpmnExtensions>(StringComparer.Ordinal);

        // The container's declared variables gate collection-mode multi-instance loops, so they are read
        // before the element loop that resolves those loops.
        var declaredVariables = ReadDeclaredVariables(container);
        var declaredVariableNames = declaredVariables.Select(variable => variable.Name).ToHashSet(StringComparer.Ordinal);

        // Per-scope event-subprocess trackers: escalation codes must be distinct with at most one code-less
        // catch-all, and a scope carries at most one error-triggered event subprocess.
        var eventSubprocessEscalationCodes = new HashSet<string>(StringComparer.Ordinal);
        var hasEventSubprocessEscalationCatchAll = false;
        var hasEventSubprocessError = false;

        foreach (var child in container.Elements().Where(child => child.Name.Namespace == BpmnXmlNames.Model))
        {
            var localName = child.Name.LocalName;
            var id = IdOf(child);
            context.CountElement(localName);

            if (id is not null && IsRetainableFlowNode(localName)
                && BpmnExtensionCapture.Capture(child, context.Fidelity, IsFlowNodeChildConsumed, context.Retention) is { IsEmpty: false } captured)
                retainedByElementId[id] = captured;

            switch (localName)
            {
                case "startEvent":
                {
                    if (id is null) break;
                    // An event-subprocess body start declares an escalation or error trigger plus
                    // isInterrupting (default true). A ref-less escalation start is the code-less catch-all.
                    if (child.Element(BpmnXmlNames.Model + "escalationEventDefinition") is { } startEscalationDefinition)
                    {
                        var code = ResolveEscalationRefCode(startEscalationDefinition, escalationDeclarations);
                        var properties = code is null ? null : EscalationProperties(code, ResolveEscalationRefName(startEscalationDefinition, escalationDeclarations));
                        elements.Add(new BpmnElement(id, BpmnElementTypes.StartEvent, name: NameOf(child),
                            eventDefinitions: [new BpmnEventDefinition(BpmnEventDefinitionTypes.Escalation, properties)],
                            cancelActivity: (bool?)child.Attribute("isInterrupting") ?? true));
                        break;
                    }
                    if (child.Element(BpmnXmlNames.Model + "errorEventDefinition") is not null)
                    {
                        elements.Add(new BpmnElement(id, BpmnElementTypes.StartEvent, name: NameOf(child),
                            eventDefinitions: [new BpmnEventDefinition(BpmnEventDefinitionTypes.Error)],
                            cancelActivity: (bool?)child.Attribute("isInterrupting") ?? true));
                        break;
                    }
                    // A message, signal, or timer event-subprocess body start declares its trigger plus
                    // isInterrupting, and uses a one-shot <timeDuration> rather than the recurring <timeCycle>
                    // a process-level timer start uses. A body start whose trigger is unusable degrades to a
                    // plain start, which then drops the whole event subprocess.
                    if (isEventSubprocessBody)
                    {
                        var bodyStartDefinition = ResolveEventSubprocessBodyStartDefinition(id, child, messageSignalNames, context);
                        elements.Add(new BpmnElement(id, BpmnElementTypes.StartEvent, name: NameOf(child),
                            eventDefinitions: bodyStartDefinition is null ? null : [bodyStartDefinition],
                            cancelActivity: (bool?)child.Attribute("isInterrupting") ?? true));
                        break;
                    }
                    var startDefinitions = child.Elements().Where(IsEventDefinition).ToArray();
                    var startDefinition = startDefinitions.Length == 0
                        ? null
                        : ResolveStartEventDefinition(id, startDefinitions, messageSignalNames, context);
                    elements.Add(new BpmnElement(
                        id,
                        BpmnElementTypes.StartEvent,
                        name: NameOf(child),
                        eventDefinitions: startDefinition is null ? null : [startDefinition]));
                    break;
                }
                case "intermediateCatchEvent":
                {
                    if (id is null) break;
                    var resolved = ResolveCatchEvent(id, child, processId, messageSignalNames, context);
                    if (resolved is not { } catchImport)
                        break; // Dropped, with a finding; its sequence flows cascade-drop as unresolved refs.
                    elements.Add(new BpmnElement(
                        id,
                        BpmnElementTypes.IntermediateCatchEvent,
                        name: NameOf(child),
                        bindingRef: catchImport.BindingRef,
                        defaultFlowId: DefaultOf(child),
                        eventDefinitions: [catchImport.Definition]));
                    break;
                }
                case "endEvent":
                {
                    if (id is null) break;
                    // A compensate end resolves later: its activityRef targets a host whose compensation
                    // boundary is only known after the boundary pass.
                    if (child.Element(BpmnXmlNames.Model + "compensateEventDefinition") is not null)
                    {
                        pendingCompensateEnds.Add(child);
                        break;
                    }
                    if (child.Element(BpmnXmlNames.Model + "escalationEventDefinition") is { } escalationEndDefinition)
                    {
                        elements.Add(ResolveEscalationEnd(id, child, escalationEndDefinition, escalationDeclarations, context));
                        break;
                    }
                    // A message end event publishes and then ends. A message it cannot name degrades to a
                    // plain end event; an end event has no outgoing flows to cascade.
                    if (child.Element(BpmnXmlNames.Model + "messageEventDefinition") is { } messageEndDefinition)
                    {
                        var name = ResolveMessageSignalName(messageEndDefinition, BpmnEventDefinitionTypes.Message, messageSignalNames);
                        if (name is null)
                        {
                            context.Report(BpmnImportIssueSeverity.Degraded, $"Message end event '{id}' declares a message event definition with no resolvable name (missing or unresolvable messageRef); it read as a plain end event.", id);
                            elements.Add(new BpmnElement(id, BpmnElementTypes.EndEvent, name: NameOf(child)));
                            break;
                        }

                        var bindingRef = context.Bind(new BpmnWorkBinding.MessagePublish(processId, id, context.BindingRefFor(id), BpmnBindingSlot.Primary, name));
                        elements.Add(new BpmnElement(id, BpmnElementTypes.EndEvent, name: NameOf(child),
                            bindingRef: bindingRef,
                            eventDefinitions: [new BpmnEventDefinition(BpmnEventDefinitionTypes.Message, new Dictionary<string, string> { [BpmnEventDefinitionProperties.Name] = name })]));
                        break;
                    }
                    // A cancel end event is only meaningful inside a transaction; outside one it degrades.
                    if (child.Element(BpmnXmlNames.Model + "cancelEventDefinition") is not null)
                    {
                        if (isTransaction)
                        {
                            elements.Add(new BpmnElement(id, BpmnElementTypes.EndEvent, name: NameOf(child),
                                eventDefinitions: [new BpmnEventDefinition(BpmnEventDefinitionTypes.Cancel)]));
                            break;
                        }

                        context.Report(BpmnImportIssueSeverity.Degraded, $"End event '{id}' declares a cancel event definition but is not inside a transaction; it read as a plain end event.", id);
                        elements.Add(new BpmnElement(id, BpmnElementTypes.EndEvent, name: NameOf(child)));
                        break;
                    }
                    var isTerminate = child.Elements(BpmnXmlNames.Model + "terminateEventDefinition").Any();
                    var otherDefinitions = child.Elements().Where(IsEventDefinition).Any(definition => definition.Name.LocalName != "terminateEventDefinition");
                    if (otherDefinitions)
                        context.Report(BpmnImportIssueSeverity.Degraded, $"End event '{id}' declares unsupported event definitions; it read as a {(isTerminate ? "terminate" : "plain")} end event.", id);
                    elements.Add(new BpmnElement(
                        id,
                        BpmnElementTypes.EndEvent,
                        name: NameOf(child),
                        eventDefinitions: isTerminate ? [new BpmnEventDefinition(BpmnEventDefinitionTypes.Terminate)] : null));
                    break;
                }
                case "intermediateThrowEvent":
                {
                    if (id is null) break;
                    // An escalation throw must say what it escalates; a ref-less one is dropped and its flows
                    // cascade-drop as unresolved references.
                    if (child.Element(BpmnXmlNames.Model + "escalationEventDefinition") is { } escalationThrowDefinition)
                    {
                        if (ResolveEscalationThrow(id, child, escalationThrowDefinition, escalationDeclarations, context) is { } escalationThrow)
                            elements.Add(escalationThrow);
                        break;
                    }
                    if (child.Element(BpmnXmlNames.Model + "messageEventDefinition") is { } messageThrowDefinition)
                    {
                        var name = ResolveMessageSignalName(messageThrowDefinition, BpmnEventDefinitionTypes.Message, messageSignalNames);
                        if (name is null)
                        {
                            context.Report(BpmnImportIssueSeverity.Dropped, $"Message throw event '{id}' declares a message event definition with no resolvable name (missing or unresolvable messageRef); a throw must say what it publishes, so it was dropped.", id);
                            break;
                        }

                        var bindingRef = context.Bind(new BpmnWorkBinding.MessagePublish(processId, id, context.BindingRefFor(id), BpmnBindingSlot.Primary, name));
                        elements.Add(new BpmnElement(id, BpmnElementTypes.IntermediateThrowEvent, name: NameOf(child),
                            bindingRef: bindingRef, defaultFlowId: DefaultOf(child),
                            eventDefinitions: [new BpmnEventDefinition(BpmnEventDefinitionTypes.Message, new Dictionary<string, string> { [BpmnEventDefinitionProperties.Name] = name })]));
                        break;
                    }
                    // Anything else is resolved in the compensate pass: a compensate throw with a resolvable
                    // activityRef is kept, everything else drops.
                    pendingCompensateThrows.Add(child);
                    break;
                }
                case "association":
                {
                    var sourceRef = ((string?)child.Attribute("sourceRef"))?.Trim();
                    var targetRef = ((string?)child.Attribute("targetRef"))?.Trim();
                    if (!string.IsNullOrWhiteSpace(sourceRef) && !string.IsNullOrWhiteSpace(targetRef))
                        associations.Add((sourceRef, targetRef));
                    break;
                }
                case "subProcess":
                case "transaction":
                {
                    if (id is null) break;
                    var isTransactionHost = localName == "transaction";
                    var isEventSubprocess = !isTransactionHost && (bool?)child.Attribute("triggeredByEvent") == true;
                    var bindingMark = context.Bindings.Count;
                    var nested = BuildProcess(child, id, isExecutable, messageSignalNames, escalationDeclarations, context,
                        isTransaction: isTransactionHost, isEventSubprocessBody: isEventSubprocess);
                    context.CurrentProcessId = processId;

                    // An event subprocess is validated before its element is emitted, so a shape no
                    // interpreter could run is never produced. A message, signal, or timer trigger also arms
                    // a scope listener, which is a second binding on the same element.
                    if (isEventSubprocess)
                    {
                        if (TryResolveEventSubprocess(id, nested, processId, eventSubprocessEscalationCodes, ref hasEventSubprocessEscalationCatchAll, ref hasEventSubprocessError, context) is not { } eventSubprocess)
                        {
                            context.DropBindingsFrom(bindingMark);
                            break; // Dropped, with a finding; its flows cascade-drop.
                        }

                        var bodyRef = context.Bind(new BpmnWorkBinding.NestedProcess(processId, id, context.BindingRefFor(id), BpmnBindingSlot.Primary, nested));
                        elements.Add(new BpmnElement(id, BpmnElementTypes.SubProcess, name: NameOf(child), bindingRef: bodyRef,
                            triggeredByEvent: true, listenerBindingRef: eventSubprocess.ListenerBindingRef));
                        break;
                    }

                    var nestedRef = context.Bind(new BpmnWorkBinding.NestedProcess(processId, id, context.BindingRefFor(id), BpmnBindingSlot.Primary, nested));
                    elements.Add(new BpmnElement(id, BpmnElementTypes.SubProcess, name: NameOf(child), bindingRef: nestedRef, defaultFlowId: DefaultOf(child),
                        loopCharacteristics: ResolveLoopCharacteristics(child, id, isActivity: true, declaredVariableNames, context),
                        isForCompensation: IsForCompensationOf(child), isTransaction: isTransactionHost));
                    break;
                }
                case "callActivity":
                {
                    if (id is null) break;
                    ReadCallActivity(child, id, processId, declaredVariableNames, elements, context);
                    break;
                }
                case "boundaryEvent":
                {
                    if (id is null) break;
                    // Resolved in a second pass so attachment resolves regardless of document order.
                    pendingBoundaries.Add(child);
                    break;
                }
                case "sequenceFlow":
                {
                    if (id is null) break;
                    var sourceRef = (string?)child.Attribute("sourceRef");
                    var targetRef = (string?)child.Attribute("targetRef");
                    if (sourceRef is null || targetRef is null)
                    {
                        context.Report(BpmnImportIssueSeverity.Dropped, $"Sequence flow '{id}' is missing sourceRef/targetRef and was dropped.", id);
                        break;
                    }

                    var conditionOutcome = (string?)child.Attribute(BpmnXmlNames.Vendor + "conditionOutcome");
                    var conditionExpression = child.Element(BpmnXmlNames.Model + "conditionExpression");
                    if (conditionOutcome is null && conditionExpression is not null)
                        context.Report(BpmnImportIssueSeverity.Degraded, $"Sequence flow '{id}' carries an expression condition ('{conditionExpression.Value.Trim()}'); expression conditions are not evaluated by this model, so the flow read as unconditional.", id);

                    flows.Add(new BpmnSequenceFlow(id, sourceRef, targetRef, name: NameOf(child), conditionOutcome: conditionOutcome,
                        extensions: BpmnExtensionCapture.Capture(child, context.Fidelity, IsFlowNodeChildConsumed, context.Retention)));
                    break;
                }
                case "laneSet":
                {
                    foreach (var lane in child.Elements(BpmnXmlNames.Model + "lane"))
                    {
                        var laneId = IdOf(lane);
                        if (laneId is null) continue;
                        lanes.Add(new BpmnLane(laneId, name: NameOf(lane)));
                        foreach (var flowNodeRef in lane.Elements(BpmnXmlNames.Model + "flowNodeRef"))
                            context.LaneByElementId[flowNodeRef.Value.Trim()] = laneId;
                    }
                    break;
                }
                default:
                {
                    if (BpmnXmlNames.TaskLocalNamesToElementTypes.TryGetValue(localName, out var taskType))
                    {
                        if (id is null) break;
                        // A send or receive task carries its message on a messageRef attribute rather than a
                        // nested event definition. With a resolvable name it binds a publish or a wait; a
                        // name-less one falls through to the unbound path below.
                        var isSendTask = StringComparer.Ordinal.Equals(taskType, BpmnElementTypes.SendTask);
                        var isReceiveTask = StringComparer.Ordinal.Equals(taskType, BpmnElementTypes.ReceiveTask);
                        if ((isSendTask || isReceiveTask)
                            && ResolveMessageSignalName(child, BpmnEventDefinitionTypes.Message, messageSignalNames) is { } messageTaskName)
                        {
                            var taskBindingRef = context.BindingRefFor(id);
                            context.Bind(isSendTask
                                ? new BpmnWorkBinding.MessagePublish(processId, id, taskBindingRef, BpmnBindingSlot.Primary, messageTaskName)
                                : new BpmnWorkBinding.MessageWait(processId, id, taskBindingRef, BpmnBindingSlot.Primary, messageTaskName));
                            elements.Add(new BpmnElement(id, taskType, name: NameOf(child), bindingRef: taskBindingRef, defaultFlowId: DefaultOf(child),
                                properties: new Dictionary<string, string> { [MessageNamePropertyKey] = messageTaskName },
                                loopCharacteristics: ResolveLoopCharacteristics(child, id, isActivity: true, declaredVariableNames, context)));
                            break;
                        }

                        var unboundRef = context.Bind(new BpmnWorkBinding.UnboundTask(processId, id, context.BindingRefFor(id), BpmnBindingSlot.Primary, taskType));
                        elements.Add(new BpmnElement(id, taskType, name: NameOf(child), bindingRef: unboundRef, defaultFlowId: DefaultOf(child),
                            loopCharacteristics: ResolveLoopCharacteristics(child, id, isActivity: true, declaredVariableNames, context),
                            isForCompensation: IsForCompensationOf(child)));
                        if (taskType != BpmnElementTypes.Task)
                            context.Report(BpmnImportIssueSeverity.Info, $"{Capitalize(localName)} '{id}' declares no implementation; the host must supply the work bound to it.", id);
                        break;
                    }

                    if (BpmnXmlNames.GatewayLocalNamesToElementTypes.TryGetValue(localName, out var gatewayType))
                    {
                        if (id is null) break;
                        ResolveLoopCharacteristics(child, id, isActivity: false, declaredVariableNames, context);
                        elements.Add(new BpmnElement(id, gatewayType, name: NameOf(child), defaultFlowId: DefaultOf(child)));
                        break;
                    }

                    if (localName is "documentation" or "extensionElements" or "incoming" or "outgoing")
                        break;

                    context.Report(BpmnImportIssueSeverity.Dropped, $"BPMN element <{localName}>{(id is null ? "" : $" '{id}'")} is not part of the model this reader builds and was dropped.", id);
                    break;
                }
            }
        }

        // Second pass: boundary events resolve against the now-complete host set.
        var elementsById = elements.ToDictionary(element => element.ElementId, StringComparer.Ordinal);
        var referencedHandlerIds = new HashSet<string>(StringComparer.Ordinal);
        // An element that takes part in any sequence flow can never be a compensation handler, because a
        // handler runs only under compensation replay. Excluding flow participants here means the boundary
        // drops and the element stays an ordinary flow element.
        var flowParticipantIds = flows
            .SelectMany(flow => new[] { flow.SourceRef, flow.TargetRef })
            .ToHashSet(StringComparer.Ordinal);
        var transactionHostsWithCancelBoundary = new HashSet<string>(StringComparer.Ordinal);
        var escalationCodesByHost = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var escalationCatchAllHosts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var boundaryXml in pendingBoundaries)
        {
            var boundary = ResolveBoundaryEvent(boundaryXml, processId, elementsById, messageSignalNames, escalationDeclarations, associations, flowParticipantIds, transactionHostsWithCancelBoundary, escalationCodesByHost, escalationCatchAllHosts, context);
            if (boundary is null)
                continue; // Dropped, with a finding; its sequence flows cascade-drop as unresolved refs.
            elements.Add(boundary);

            if (boundary.CompensationHandlerElementId is { } handlerId)
            {
                referencedHandlerIds.Add(handlerId);
                MarkHandlerForCompensation(elements, handlerId);
            }
        }

        // Third pass: compensate throw and end events resolve against the full element set plus the hosts
        // that turned out to carry a compensation boundary.
        var compensationHostIds = elements
            .Where(element => element.CompensationHandlerElementId is not null && element.AttachedToRef is not null)
            .Select(element => element.AttachedToRef!)
            .ToHashSet(StringComparer.Ordinal);
        var readElementIds = elements.Select(element => element.ElementId).ToHashSet(StringComparer.Ordinal);
        foreach (var throwXml in pendingCompensateThrows)
        {
            if (ResolveCompensateThrow(throwXml, readElementIds, compensationHostIds, context) is { } throwElement)
                elements.Add(throwElement);
        }
        foreach (var endXml in pendingCompensateEnds)
            elements.Add(ResolveCompensateEnd(endXml, readElementIds, compensationHostIds, context));

        // An isForCompensation activity that no compensation boundary references can never be reached: it
        // takes part in no flow and nothing compensates it.
        foreach (var orphan in elements.Where(element => element.IsForCompensation && !referencedHandlerIds.Contains(element.ElementId)).ToArray())
        {
            elements.Remove(orphan);
            context.DropBindingsFor(orphan);
            context.Report(BpmnImportIssueSeverity.Dropped, $"Activity '{orphan.ElementId}' is marked isForCompensation but is referenced by no compensation boundary; it cannot take part in normal flow and was dropped.", orphan.ElementId);
        }

        var finishedElements = elements
            .Select(element =>
            {
                var laneId = context.LaneByElementId.TryGetValue(element.ElementId, out var lane) ? lane : null;
                var retained = retainedByElementId.TryGetValue(element.ElementId, out var extensions) ? extensions : null;
                return laneId is null && retained is null ? element : CopyElement(element, laneId, extensions: retained);
            })
            .ToArray();

        var elementIds = finishedElements.Select(element => element.ElementId).ToHashSet(StringComparer.Ordinal);
        var connectedFlows = flows.Where(flow =>
        {
            var connected = elementIds.Contains(flow.SourceRef) && elementIds.Contains(flow.TargetRef);
            if (!connected)
                context.Report(BpmnImportIssueSeverity.Dropped, $"Sequence flow '{flow.FlowId}' references a dropped element and was dropped with it.", flow.FlowId);
            return connected;
        }).ToArray();

        var extensions = BpmnExtensionCapture.Capture(container, context.Fidelity, IsContainerChildConsumed, context.Retention);
        context.CurrentProcessId = previousProcessId;

        return new BpmnProcessDefinition(
            processId,
            NameOf(container),
            isExecutable,
            isTransaction,
            finishedElements,
            connectedFlows,
            lanes,
            declaredVariables,
            extensions);
    }

    // ---------------------------------------------------------------------------------------------------
    // Document-level declarations
    // ---------------------------------------------------------------------------------------------------

    private static Dictionary<string, string?> ReadNamedDeclarations(XElement root, string localName)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var declaration in root.Elements(BpmnXmlNames.Model + localName))
            if (IdOf(declaration) is { } id)
                result[id] = NameOf(declaration);
        return result;
    }

    /// <summary>
    /// The root <c>&lt;message&gt;</c> and <c>&lt;signal&gt;</c> index (id to name) a <c>messageRef</c> or
    /// <c>signalRef</c> resolves through to the name events are actually matched on.
    /// </summary>
    private static IReadOnlyDictionary<string, string> ReadMessageSignalNames(XElement root)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var declaration in root.Elements().Where(element =>
                     element.Name.Namespace == BpmnXmlNames.Model &&
                     element.Name.LocalName is "message" or "signal"))
        {
            var declarationId = IdOf(declaration);
            var name = NameOf(declaration);
            if (declarationId is not null && name is not null)
                result[declarationId] = name;
        }

        return result;
    }

    private static IReadOnlyList<BpmnErrorDeclaration> ReadErrorDeclarations(XElement root)
    {
        var result = new List<BpmnErrorDeclaration>();
        foreach (var declaration in root.Elements(BpmnXmlNames.Model + "error"))
        {
            if (IdOf(declaration) is not { } id) continue;
            var code = ((string?)declaration.Attribute("errorCode"))?.Trim();
            result.Add(new BpmnErrorDeclaration(id, NameOf(declaration), string.IsNullOrWhiteSpace(code) ? null : code));
        }

        return result;
    }

    /// <summary>
    /// The root <c>&lt;escalation&gt;</c> index. An <c>escalationRef</c> resolves through this to the code
    /// escalation catchers match on, falling back to the declaration's name and then to the ref id itself.
    /// </summary>
    private static IReadOnlyDictionary<string, EscalationDeclaration> ReadEscalationDeclarations(XElement root)
    {
        var result = new Dictionary<string, EscalationDeclaration>(StringComparer.Ordinal);
        foreach (var declaration in root.Elements(BpmnXmlNames.Model + "escalation"))
        {
            if (IdOf(declaration) is not { } declarationId)
                continue;
            var code = ((string?)declaration.Attribute("escalationCode"))?.Trim();
            var name = NameOf(declaration)?.Trim();
            result[declarationId] = new EscalationDeclaration(
                string.IsNullOrWhiteSpace(code) ? null : code,
                string.IsNullOrWhiteSpace(name) ? null : name);
        }

        return result;
    }

    private static IReadOnlyList<ParticipantDeclaration> ReadParticipants(XElement root)
    {
        var result = new List<ParticipantDeclaration>();
        foreach (var collaboration in root.Elements(BpmnXmlNames.Model + "collaboration"))
            foreach (var participant in collaboration.Elements(BpmnXmlNames.Model + "participant"))
            {
                if (IdOf(participant) is not { } id)
                    continue;
                var processRef = ((string?)participant.Attribute("processRef"))?.Trim();
                result.Add(new ParticipantDeclaration(id, NameOf(participant), string.IsNullOrWhiteSpace(processRef) ? null : processRef));
            }

        return result;
    }

    private static IReadOnlyList<MessageFlowDeclaration> ReadMessageFlows(XElement root)
    {
        var result = new List<MessageFlowDeclaration>();
        foreach (var collaboration in root.Elements(BpmnXmlNames.Model + "collaboration"))
            foreach (var flow in collaboration.Elements(BpmnXmlNames.Model + "messageFlow"))
            {
                if (IdOf(flow) is not { } id)
                    continue;
                var messageRef = ((string?)flow.Attribute("messageRef"))?.Trim();
                result.Add(new MessageFlowDeclaration(
                    id,
                    NameOf(flow),
                    ((string?)flow.Attribute("sourceRef"))?.Trim() ?? "",
                    ((string?)flow.Attribute("targetRef"))?.Trim() ?? "",
                    string.IsNullOrWhiteSpace(messageRef) ? null : messageRef));
            }

        return result;
    }

    /// <summary>
    /// Reads a container's declared variables from its
    /// <c>&lt;extensionElements&gt;&lt;vw:variable name="..."/&gt;</c> declarations. BPMN has no standard
    /// container-scoped variable declaration, so this is the vendor representation; the name is what gates a
    /// collection-mode multi-instance loop and what survives the round-trip.
    /// </summary>
    private static IReadOnlyList<BpmnVariableDeclaration> ReadDeclaredVariables(XElement container)
    {
        var extensions = container.Element(BpmnXmlNames.Model + "extensionElements");
        if (extensions is null)
            return [];

        var variables = new List<BpmnVariableDeclaration>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var declaration in extensions.Elements(BpmnXmlNames.Vendor + "variable"))
        {
            if (((string?)declaration.Attribute("name"))?.Trim() is not { Length: > 0 } name || !seen.Add(name))
                continue;
            var typeHint = ((string?)declaration.Attribute("typeHint"))?.Trim();
            variables.Add(new BpmnVariableDeclaration(name, string.IsNullOrWhiteSpace(typeHint) ? null : typeHint));
        }

        return variables;
    }

    // ---------------------------------------------------------------------------------------------------
    // Diagram interchange
    // ---------------------------------------------------------------------------------------------------

    private static IReadOnlyList<BpmnDiagram> ReadDiagrams(XElement root)
    {
        var diagrams = new List<BpmnDiagram>();
        foreach (var diagram in root.Elements(BpmnXmlNames.Di + "BPMNDiagram"))
        {
            if (diagram.Element(BpmnXmlNames.Di + "BPMNPlane") is not { } planeElement)
                continue;

            var shapes = new List<BpmnShape>();
            foreach (var shape in planeElement.Elements(BpmnXmlNames.Di + "BPMNShape"))
            {
                var reference = ((string?)shape.Attribute("bpmnElement"))?.Trim();
                if (string.IsNullOrEmpty(reference) || ReadBounds(shape.Element(BpmnXmlNames.Dc + "Bounds")) is not { } bounds)
                    continue;
                shapes.Add(new BpmnShape(
                    IdOf(shape),
                    reference,
                    bounds,
                    (bool?)shape.Attribute("isHorizontal"),
                    (bool?)shape.Attribute("isExpanded"),
                    (bool?)shape.Attribute("isMarkerVisible"),
                    ReadLabel(shape)));
            }

            var edges = new List<BpmnEdge>();
            foreach (var edge in planeElement.Elements(BpmnXmlNames.Di + "BPMNEdge"))
            {
                var reference = ((string?)edge.Attribute("bpmnElement"))?.Trim();
                if (string.IsNullOrEmpty(reference))
                    continue;
                var waypoints = edge.Elements(BpmnXmlNames.Dd + "waypoint")
                    .Select(waypoint => new BpmnPoint((double?)waypoint.Attribute("x") ?? 0, (double?)waypoint.Attribute("y") ?? 0))
                    .ToArray();
                edges.Add(new BpmnEdge(IdOf(edge), reference, waypoints, ReadLabel(edge)));
            }

            diagrams.Add(new BpmnDiagram(
                IdOf(diagram),
                NameOf(diagram),
                new BpmnPlane(IdOf(planeElement), ((string?)planeElement.Attribute("bpmnElement"))?.Trim(), shapes, edges)));
        }

        return diagrams;
    }

    private static BpmnBounds? ReadBounds(XElement? bounds) =>
        bounds is null
            ? null
            : new BpmnBounds(
                (double?)bounds.Attribute("x") ?? 0,
                (double?)bounds.Attribute("y") ?? 0,
                (double?)bounds.Attribute("width") ?? 0,
                (double?)bounds.Attribute("height") ?? 0);

    private static BpmnLabel? ReadLabel(XElement owner) =>
        owner.Element(BpmnXmlNames.Di + "BPMNLabel") is { } label
            ? new BpmnLabel(ReadBounds(label.Element(BpmnXmlNames.Dc + "Bounds")))
            : null;

    // ---------------------------------------------------------------------------------------------------
    // Event resolution
    // ---------------------------------------------------------------------------------------------------

    private static string? ResolveEscalationRefCode(XElement definition, IReadOnlyDictionary<string, EscalationDeclaration> escalationDeclarations)
    {
        if (((string?)definition.Attribute("escalationRef"))?.Trim() is not { Length: > 0 } escalationRef)
            return null;

        if (escalationDeclarations.TryGetValue(escalationRef, out var declaration))
            return declaration.Code ?? declaration.Name ?? escalationRef;

        return escalationRef;
    }

    private static BpmnElement? ResolveEscalationThrow(string id, XElement element, XElement definition, IReadOnlyDictionary<string, EscalationDeclaration> escalationDeclarations, ReadContext context)
    {
        var code = ResolveEscalationRefCode(definition, escalationDeclarations);
        if (code is null)
        {
            context.Report(BpmnImportIssueSeverity.Dropped, $"Escalation throw event '{id}' declares no escalationRef; a throw must say what it escalates, so it was dropped.", id);
            return null;
        }

        return new BpmnElement(id, BpmnElementTypes.IntermediateThrowEvent, name: NameOf(element),
            eventDefinitions: [new BpmnEventDefinition(BpmnEventDefinitionTypes.Escalation, EscalationProperties(code, ResolveEscalationRefName(definition, escalationDeclarations)))]);
    }

    private static BpmnElement ResolveEscalationEnd(string id, XElement element, XElement definition, IReadOnlyDictionary<string, EscalationDeclaration> escalationDeclarations, ReadContext context)
    {
        var code = ResolveEscalationRefCode(definition, escalationDeclarations);
        if (code is null)
        {
            context.Report(BpmnImportIssueSeverity.Degraded, $"Escalation end event '{id}' declares no escalationRef; a throw must say what it escalates, so it read as a plain end event.", id);
            return new BpmnElement(id, BpmnElementTypes.EndEvent, name: NameOf(element));
        }

        return new BpmnElement(id, BpmnElementTypes.EndEvent, name: NameOf(element),
            eventDefinitions: [new BpmnEventDefinition(BpmnEventDefinitionTypes.Escalation, EscalationProperties(code, ResolveEscalationRefName(definition, escalationDeclarations)))]);
    }

    /// <summary>The listener binding an event subprocess arms, for a message, signal, or timer trigger; <c>null</c> for escalation and error, which are dormant catchers.</summary>
    private readonly record struct EventSubprocessImport(string? ListenerBindingRef);

    /// <summary>
    /// Validates an event subprocess before its element is emitted. The body must declare exactly one start
    /// event with exactly one supported trigger; per scope, escalation codes must be distinct with at most one
    /// code-less catch-all, and at most one error-triggered event subprocess may exist. A message, signal, or
    /// timer trigger additionally arms a scope listener.
    /// </summary>
    private static EventSubprocessImport? TryResolveEventSubprocess(
        string id,
        BpmnProcessDefinition body,
        string processId,
        HashSet<string> escalationCodes,
        ref bool hasEscalationCatchAll,
        ref bool hasError,
        ReadContext context)
    {
        var starts = body.Elements
            .Where(element => StringComparer.Ordinal.Equals(element.ElementType, BpmnElementTypes.StartEvent))
            .ToArray();
        if (starts.Length != 1)
        {
            context.Report(BpmnImportIssueSeverity.Dropped, $"Event subprocess '{id}' body must declare exactly one start event; it declares {starts.Length}. It was dropped.", id);
            return null;
        }

        var start = starts[0];
        if (start.EventDefinitions.Count != 1)
        {
            context.Report(BpmnImportIssueSeverity.Dropped, $"Event subprocess '{id}' body start event must declare exactly one supported trigger definition (escalation, error, message, signal, or timer); it declares {start.EventDefinitions.Count}. It was dropped.", id);
            return null;
        }

        var definition = start.EventDefinitions.Single();
        var interrupting = start.CancelActivity;
        if (StringComparer.Ordinal.Equals(definition.Type, BpmnEventDefinitionTypes.Escalation))
        {
            var code = definition.Properties.TryGetValue(BpmnEventDefinitionProperties.Code, out var codeValue) && !string.IsNullOrWhiteSpace(codeValue) ? codeValue.Trim() : null;
            if (code is null)
            {
                if (hasEscalationCatchAll)
                {
                    context.Report(BpmnImportIssueSeverity.Dropped, $"Event subprocess '{id}' is a second code-less catch-all escalation event subprocess in its scope, which may carry at most one; it was dropped.", id);
                    return null;
                }
                hasEscalationCatchAll = true;
            }
            else if (!escalationCodes.Add(code))
            {
                context.Report(BpmnImportIssueSeverity.Dropped, $"Event subprocess '{id}' declares escalation code '{code}', which another event subprocess in its scope already claims; it was dropped.", id);
                return null;
            }

            return new EventSubprocessImport(ListenerBindingRef: null);
        }

        if (StringComparer.Ordinal.Equals(definition.Type, BpmnEventDefinitionTypes.Error))
        {
            // BPMN makes catching an error always interrupting, and a scope carries at most one error-triggered
            // event subprocess; either violation would produce a shape no interpreter could run.
            if (!interrupting)
            {
                context.Report(BpmnImportIssueSeverity.Dropped, $"Event subprocess '{id}' is a non-interrupting error event subprocess; error events are always interrupting per BPMN, so it was dropped.", id);
                return null;
            }
            if (hasError)
            {
                context.Report(BpmnImportIssueSeverity.Dropped, $"Event subprocess '{id}' is a second error-triggered event subprocess in its scope, which may carry at most one; it was dropped.", id);
                return null;
            }
            hasError = true;
            return new EventSubprocessImport(ListenerBindingRef: null);
        }

        // A message, signal, or timer event subprocess arms a listener when its enclosing scope starts, so the
        // trigger can be observed while the scope runs. The body start already resolved the trigger facts; a
        // body start whose facts were unusable degraded to a plain start and was rejected above.
        var listenerRef = context.BindingRefFor($"{id}-listener");
        if (StringComparer.Ordinal.Equals(definition.Type, BpmnEventDefinitionTypes.Message))
        {
            context.Bind(new BpmnWorkBinding.MessageWait(processId, id, listenerRef, BpmnBindingSlot.ScopeListener, definition.Properties[BpmnEventDefinitionProperties.Name]));
            return new EventSubprocessImport(listenerRef);
        }

        if (StringComparer.Ordinal.Equals(definition.Type, BpmnEventDefinitionTypes.Signal))
        {
            context.Bind(new BpmnWorkBinding.SignalWait(processId, id, listenerRef, BpmnBindingSlot.ScopeListener, definition.Properties[BpmnEventDefinitionProperties.Name]));
            return new EventSubprocessImport(listenerRef);
        }

        if (StringComparer.Ordinal.Equals(definition.Type, BpmnEventDefinitionTypes.Timer))
        {
            context.Bind(new BpmnWorkBinding.TimerWait(processId, id, listenerRef, BpmnBindingSlot.ScopeListener, definition.Properties[BpmnEventDefinitionProperties.Interval]));
            return new EventSubprocessImport(listenerRef);
        }

        context.Report(BpmnImportIssueSeverity.Dropped, $"Event subprocess '{id}' body start event declares an unsupported trigger definition '{definition.Type}'; only escalation, error, message, signal, and timer triggers are supported. It was dropped.", id);
        return null;
    }

    /// <summary>
    /// Resolves a message, signal, or timer event-subprocess body start. A timer body start is a one-shot
    /// <c>&lt;timeDuration&gt;</c>; a <c>&lt;timeCycle&gt;</c> or <c>&lt;timeDate&gt;</c> body start, or an
    /// unresolvable name, degrades to a plain start, which drops the event subprocess.
    /// </summary>
    private static BpmnEventDefinition? ResolveEventSubprocessBodyStartDefinition(string id, XElement child, IReadOnlyDictionary<string, string> messageSignalNames, ReadContext context)
    {
        if (child.Element(BpmnXmlNames.Model + "messageEventDefinition") is { } messageDefinition)
            return ResolveEventSubprocessMessageSignal(id, messageDefinition, BpmnEventDefinitionTypes.Message, messageSignalNames, context);
        if (child.Element(BpmnXmlNames.Model + "signalEventDefinition") is { } signalDefinition)
            return ResolveEventSubprocessMessageSignal(id, signalDefinition, BpmnEventDefinitionTypes.Signal, messageSignalNames, context);
        if (child.Element(BpmnXmlNames.Model + "timerEventDefinition") is { } timerDefinition)
        {
            var duration = ResolveCatchTimerDuration(timerDefinition);
            if (duration is null)
            {
                context.Report(BpmnImportIssueSeverity.Degraded, $"Event subprocess '{id}' body start declares a timer that is not a one-shot <timeDuration>; it read as a plain start and the event subprocess was dropped.", id);
                return null;
            }
            return new BpmnEventDefinition(BpmnEventDefinitionTypes.Timer, new Dictionary<string, string> { [BpmnEventDefinitionProperties.Interval] = duration });
        }

        return null;
    }

    private static BpmnEventDefinition? ResolveEventSubprocessMessageSignal(string id, XElement definition, string type, IReadOnlyDictionary<string, string> messageSignalNames, ReadContext context)
    {
        var name = ResolveMessageSignalName(definition, type, messageSignalNames);
        if (name is null)
        {
            context.Report(BpmnImportIssueSeverity.Degraded, $"Event subprocess '{id}' body start declares a {type} event definition with no resolvable name; it read as a plain start and the event subprocess was dropped.", id);
            return null;
        }

        return new BpmnEventDefinition(type, new Dictionary<string, string> { [BpmnEventDefinitionProperties.Name] = name });
    }

    private static string? ResolveEscalationRefName(XElement definition, IReadOnlyDictionary<string, EscalationDeclaration> escalationDeclarations) =>
        ((string?)definition.Attribute("escalationRef"))?.Trim() is { Length: > 0 } escalationRef
        && escalationDeclarations.TryGetValue(escalationRef, out var declaration)
            ? declaration.Name
            : null;

    private static IReadOnlyDictionary<string, string> EscalationProperties(string code, string? name)
    {
        var properties = new Dictionary<string, string>(StringComparer.Ordinal) { [BpmnEventDefinitionProperties.Code] = code };
        if (!string.IsNullOrWhiteSpace(name))
            properties[BpmnEventDefinitionProperties.Name] = name.Trim();
        return properties;
    }

    /// <summary>A root escalation declaration: its optional explicit code and display name.</summary>
    private readonly record struct EscalationDeclaration(string? Code, string? Name);

    /// <summary>
    /// Resolves the single event definition of an event-defined start event, or reports a Degraded finding and
    /// returns <c>null</c> so the element reads as a plain start.
    /// </summary>
    private static BpmnEventDefinition? ResolveStartEventDefinition(string id, IReadOnlyList<XElement> definitions, IReadOnlyDictionary<string, string> messageSignalNames, ReadContext context)
    {
        if (definitions.Count != 1)
        {
            context.Report(BpmnImportIssueSeverity.Degraded, $"Start event '{id}' declares {definitions.Count} event definitions; only a single timer, message, or signal definition is supported, so it read as a plain start event.", id);
            return null;
        }

        var definition = definitions[0];
        switch (definition.Name.LocalName)
        {
            case "messageEventDefinition":
            case "signalEventDefinition":
            {
                var type = definition.Name.LocalName == "messageEventDefinition" ? BpmnEventDefinitionTypes.Message : BpmnEventDefinitionTypes.Signal;
                var name = ResolveMessageSignalName(definition, type, messageSignalNames);
                if (name is null)
                {
                    context.Report(BpmnImportIssueSeverity.Degraded, $"Start event '{id}' declares a {type} event definition with no resolvable name (missing or unresolvable {type}Ref); it read as a plain start event.", id);
                    return null;
                }

                return new BpmnEventDefinition(type, new Dictionary<string, string> { [BpmnEventDefinitionProperties.Name] = name });
            }
            case "timerEventDefinition":
            {
                var properties = ResolveStartTimerProperties(definition);
                if (properties is null)
                {
                    context.Report(BpmnImportIssueSeverity.Degraded, $"Start event '{id}' declares a timer event definition that is not a recurring schedule; only a <timeCycle> interval or cron start is supported, so it read as a plain start event.", id);
                    return null;
                }

                return new BpmnEventDefinition(BpmnEventDefinitionTypes.Timer, properties);
            }
            default:
                context.Report(BpmnImportIssueSeverity.Degraded, $"Start event '{id}' declares an unsupported '{definition.Name.LocalName}'; only timer, message, and signal start events are supported, so it read as a plain start event.", id);
                return null;
        }
    }

    /// <summary>The event definition and binding ref of a resolved intermediate catch event.</summary>
    private readonly record struct CatchEventImport(BpmnEventDefinition Definition, string BindingRef);

    /// <summary>
    /// Resolves an intermediate catch event into its event definition plus the wait it binds, or reports a
    /// Dropped finding and returns <c>null</c> when the catch cannot form a runnable graph, in which case its
    /// sequence flows cascade-drop as unresolved references.
    /// </summary>
    private static CatchEventImport? ResolveCatchEvent(string id, XElement element, string processId, IReadOnlyDictionary<string, string> messageSignalNames, ReadContext context)
    {
        var definitions = element.Elements().Where(IsEventDefinition).ToArray();
        if (definitions.Length != 1)
        {
            context.Report(BpmnImportIssueSeverity.Dropped, $"Intermediate catch event '{id}' declares {definitions.Length} event definitions; exactly one timer, message, or signal definition is required, so it was dropped.", id);
            return null;
        }

        var definition = definitions[0];
        var bindingRef = context.BindingRefFor(id);
        switch (definition.Name.LocalName)
        {
            case "messageEventDefinition":
            case "signalEventDefinition":
            {
                var type = definition.Name.LocalName == "messageEventDefinition" ? BpmnEventDefinitionTypes.Message : BpmnEventDefinitionTypes.Signal;
                var name = ResolveMessageSignalName(definition, type, messageSignalNames);
                if (name is null)
                {
                    context.Report(BpmnImportIssueSeverity.Dropped, $"Intermediate catch event '{id}' declares a {type} event definition with no resolvable name (missing or unresolvable {type}Ref), so it was dropped.", id);
                    return null;
                }

                context.Bind(type == BpmnEventDefinitionTypes.Message
                    ? new BpmnWorkBinding.MessageWait(processId, id, bindingRef, BpmnBindingSlot.Primary, name)
                    : new BpmnWorkBinding.SignalWait(processId, id, bindingRef, BpmnBindingSlot.Primary, name));
                return new CatchEventImport(new BpmnEventDefinition(type, new Dictionary<string, string> { [BpmnEventDefinitionProperties.Name] = name }), bindingRef);
            }
            case "timerEventDefinition":
            {
                var duration = ResolveCatchTimerDuration(definition);
                if (duration is null)
                {
                    context.Report(BpmnImportIssueSeverity.Dropped, $"Intermediate catch event '{id}' declares a timer event definition without a <timeDuration>; only a one-shot duration catch timer is supported, so it was dropped.", id);
                    return null;
                }

                context.Bind(new BpmnWorkBinding.TimerWait(processId, id, bindingRef, BpmnBindingSlot.Primary, duration));
                return new CatchEventImport(new BpmnEventDefinition(BpmnEventDefinitionTypes.Timer, new Dictionary<string, string> { [BpmnEventDefinitionProperties.Interval] = duration }), bindingRef);
            }
            default:
                context.Report(BpmnImportIssueSeverity.Dropped, $"Intermediate catch event '{id}' declares an unsupported '{definition.Name.LocalName}'; only timer, message, and signal catch events are supported, so it was dropped.", id);
                return null;
        }
    }

    /// <summary>
    /// Resolves a boundary event into its element plus, for a catching boundary, the wait it binds. Returns
    /// <c>null</c> with a Dropped finding when the boundary cannot be represented: an unresolvable or
    /// unsuitable host, an unsupported definition, or a non-interrupting error boundary. Its sequence flows
    /// then cascade-drop as unresolved references.
    /// </summary>
    private static BpmnElement? ResolveBoundaryEvent(
        XElement element,
        string processId,
        IReadOnlyDictionary<string, BpmnElement> elementsById,
        IReadOnlyDictionary<string, string> messageSignalNames,
        IReadOnlyDictionary<string, EscalationDeclaration> escalationDeclarations,
        IReadOnlyList<(string Source, string Target)> associations,
        IReadOnlySet<string> flowParticipantIds,
        HashSet<string> transactionHostsWithCancelBoundary,
        Dictionary<string, HashSet<string>> escalationCodesByHost,
        HashSet<string> escalationCatchAllHosts,
        ReadContext context)
    {
        var id = IdOf(element)!;
        var attachedToRef = ((string?)element.Attribute("attachedToRef"))?.Trim();
        if (string.IsNullOrWhiteSpace(attachedToRef))
        {
            context.Report(BpmnImportIssueSeverity.Dropped, $"Boundary event '{id}' declares no attachedToRef host and was dropped.", id);
            return null;
        }

        if (!elementsById.TryGetValue(attachedToRef, out var host))
        {
            context.Report(BpmnImportIssueSeverity.Dropped, $"Boundary event '{id}' is attached to '{attachedToRef}', which is not an element of this process, and was dropped.", id);
            return null;
        }

        if (!IsBoundaryHost(host.ElementType))
        {
            context.Report(BpmnImportIssueSeverity.Dropped, $"Boundary event '{id}' is attached to '{attachedToRef}' ({host.ElementType}), which is not an activity a boundary event can attach to, and was dropped.", id);
            return null;
        }

        if (host.BindingRef is null)
        {
            context.Report(BpmnImportIssueSeverity.Dropped, $"Boundary event '{id}' is attached to host '{attachedToRef}', which binds no work for a boundary to interrupt, and was dropped.", id);
            return null;
        }

        var definitions = element.Elements().Where(IsEventDefinition).ToArray();
        if (definitions.Length != 1)
        {
            context.Report(BpmnImportIssueSeverity.Dropped, $"Boundary event '{id}' declares {definitions.Length} event definitions; exactly one timer, message, signal, or error definition is required, so it was dropped.", id);
            return null;
        }

        var cancelActivity = (bool?)element.Attribute("cancelActivity") ?? true;
        var definition = definitions[0];
        switch (definition.Name.LocalName)
        {
            case "compensateEventDefinition":
            {
                // A compensation boundary finds its handler through a boundary-to-activity association, in
                // either direction. No such association, or a handler that takes part in sequence flow or
                // binds no work, drops the boundary. cancelActivity is read as authored but has no meaning
                // for compensation, so it is not inspected here.
                var handlerId = ResolveCompensationHandler(id, associations, elementsById, flowParticipantIds);
                if (handlerId is null)
                {
                    context.Report(BpmnImportIssueSeverity.Dropped, $"Compensation boundary event '{id}' has no association to a flow-less compensation handler activity and was dropped.", id);
                    return null;
                }

                return new BpmnElement(id, BpmnElementTypes.BoundaryEvent, name: NameOf(element),
                    eventDefinitions: [new BpmnEventDefinition(BpmnEventDefinitionTypes.Compensation)],
                    attachedToRef: attachedToRef, cancelActivity: cancelActivity,
                    compensationHandlerElementId: handlerId);
            }
            case "cancelEventDefinition":
            {
                // A cancel boundary attaches only to a transaction, at most one per transaction.
                if (!host.IsTransaction)
                {
                    context.Report(BpmnImportIssueSeverity.Dropped, $"Cancel boundary event '{id}' is attached to '{attachedToRef}', which is not a transaction; it was dropped.", id);
                    return null;
                }

                if (!transactionHostsWithCancelBoundary.Add(attachedToRef))
                {
                    context.Report(BpmnImportIssueSeverity.Dropped, $"Cancel boundary event '{id}' is a second cancel boundary on transaction '{attachedToRef}', which may carry at most one; it was dropped.", id);
                    return null;
                }

                return new BpmnElement(id, BpmnElementTypes.BoundaryEvent, name: NameOf(element),
                    eventDefinitions: [new BpmnEventDefinition(BpmnEventDefinitionTypes.Cancel)],
                    attachedToRef: attachedToRef, cancelActivity: cancelActivity);
            }
            case "escalationEventDefinition":
            {
                // Only something that contains a process - a subprocess or a call activity - can escalate
                // outward, so an escalation boundary on a plain task could never fire. A ref-less boundary is
                // the code-less catch-all; a code collision or a second catch-all on one host drops.
                if (!StringComparer.Ordinal.Equals(host.ElementType, BpmnElementTypes.SubProcess)
                    && !StringComparer.Ordinal.Equals(host.ElementType, BpmnElementTypes.CallActivity))
                {
                    context.Report(BpmnImportIssueSeverity.Dropped, $"Escalation boundary event '{id}' is attached to '{attachedToRef}' ({host.ElementType}), which contains no process that could escalate outward; it was dropped.", id);
                    return null;
                }

                var code = ResolveEscalationRefCode(definition, escalationDeclarations);
                if (code is null)
                {
                    if (!escalationCatchAllHosts.Add(attachedToRef))
                    {
                        context.Report(BpmnImportIssueSeverity.Dropped, $"Escalation boundary event '{id}' is a second code-less catch-all on host '{attachedToRef}', which may carry at most one; it was dropped.", id);
                        return null;
                    }
                }
                else
                {
                    var codes = escalationCodesByHost.TryGetValue(attachedToRef, out var existing) ? existing : escalationCodesByHost[attachedToRef] = new HashSet<string>(StringComparer.Ordinal);
                    if (!codes.Add(code))
                    {
                        context.Report(BpmnImportIssueSeverity.Dropped, $"Escalation boundary event '{id}' declares code '{code}', which another escalation boundary on host '{attachedToRef}' already claims; it was dropped.", id);
                        return null;
                    }
                }

                var escalationProperties = code is null ? null : new Dictionary<string, string> { [BpmnEventDefinitionProperties.Code] = code };
                return new BpmnElement(id, BpmnElementTypes.BoundaryEvent, name: NameOf(element),
                    eventDefinitions: [new BpmnEventDefinition(BpmnEventDefinitionTypes.Escalation, escalationProperties)],
                    attachedToRef: attachedToRef, cancelActivity: cancelActivity);
            }
            case "errorEventDefinition":
            {
                if (!cancelActivity)
                {
                    context.Report(BpmnImportIssueSeverity.Dropped, $"Boundary event '{id}' is a non-interrupting error boundary, which BPMN does not allow; it was dropped.", id);
                    return null;
                }

                var properties = new Dictionary<string, string>(StringComparer.Ordinal);
                if (((string?)definition.Attribute("errorRef"))?.Trim() is { Length: > 0 } errorRef)
                    properties[ErrorRefPropertyKey] = errorRef;
                return new BpmnElement(id, BpmnElementTypes.BoundaryEvent, name: NameOf(element),
                    eventDefinitions: [new BpmnEventDefinition(BpmnEventDefinitionTypes.Error)],
                    properties: properties.Count == 0 ? null : properties,
                    attachedToRef: attachedToRef, cancelActivity: true);
            }
            case "messageEventDefinition":
            case "signalEventDefinition":
            {
                var type = definition.Name.LocalName == "messageEventDefinition" ? BpmnEventDefinitionTypes.Message : BpmnEventDefinitionTypes.Signal;
                var name = ResolveMessageSignalName(definition, type, messageSignalNames);
                if (name is null)
                {
                    context.Report(BpmnImportIssueSeverity.Dropped, $"Boundary event '{id}' declares a {type} event definition with no resolvable name (missing or unresolvable {type}Ref), so it was dropped.", id);
                    return null;
                }

                var messageBindingRef = context.BindingRefFor(id);
                context.Bind(type == BpmnEventDefinitionTypes.Message
                    ? new BpmnWorkBinding.MessageWait(processId, id, messageBindingRef, BpmnBindingSlot.Primary, name)
                    : new BpmnWorkBinding.SignalWait(processId, id, messageBindingRef, BpmnBindingSlot.Primary, name));
                return new BpmnElement(id, BpmnElementTypes.BoundaryEvent, name: NameOf(element), bindingRef: messageBindingRef,
                    eventDefinitions: [new BpmnEventDefinition(type, new Dictionary<string, string> { [BpmnEventDefinitionProperties.Name] = name })],
                    attachedToRef: attachedToRef, cancelActivity: cancelActivity);
            }
            case "timerEventDefinition":
            {
                var duration = ResolveCatchTimerDuration(definition);
                if (duration is null)
                {
                    context.Report(BpmnImportIssueSeverity.Dropped, $"Boundary event '{id}' declares a timer event definition without a <timeDuration>; only a one-shot duration boundary timer is supported, so it was dropped.", id);
                    return null;
                }

                var timerBindingRef = context.Bind(new BpmnWorkBinding.TimerWait(processId, id, context.BindingRefFor(id), BpmnBindingSlot.Primary, duration));
                return new BpmnElement(id, BpmnElementTypes.BoundaryEvent, name: NameOf(element), bindingRef: timerBindingRef,
                    eventDefinitions: [new BpmnEventDefinition(BpmnEventDefinitionTypes.Timer, new Dictionary<string, string> { [BpmnEventDefinitionProperties.Interval] = duration })],
                    attachedToRef: attachedToRef, cancelActivity: cancelActivity);
            }
            default:
                context.Report(BpmnImportIssueSeverity.Dropped, $"Boundary event '{id}' declares an unsupported '{definition.Name.LocalName}'; only timer, message, signal, error, escalation, compensation, and cancel boundary events are supported, so it was dropped.", id);
                return null;
        }
    }

    /// <summary>
    /// Finds a compensation boundary's handler through a boundary-to-activity association in either
    /// direction. The handler must be a task-family or subprocess element that binds work and takes part in no
    /// sequence flow, because a handler runs only under compensation replay.
    /// </summary>
    private static string? ResolveCompensationHandler(
        string boundaryId,
        IReadOnlyList<(string Source, string Target)> associations,
        IReadOnlyDictionary<string, BpmnElement> elementsById,
        IReadOnlySet<string> flowParticipantIds)
    {
        foreach (var (source, target) in associations)
        {
            var other = StringComparer.Ordinal.Equals(source, boundaryId) ? target
                : StringComparer.Ordinal.Equals(target, boundaryId) ? source
                : null;
            if (other is null || !elementsById.TryGetValue(other, out var candidate) || flowParticipantIds.Contains(other))
                continue;
            if (IsBoundaryHost(candidate.ElementType) && candidate.BindingRef is not null)
                return other;
        }

        return null;
    }

    private static void MarkHandlerForCompensation(List<BpmnElement> elements, string handlerId)
    {
        var index = elements.FindIndex(element => StringComparer.Ordinal.Equals(element.ElementId, handlerId));
        if (index < 0 || elements[index].IsForCompensation)
            return;
        elements[index] = CopyElement(elements[index], isForCompensation: true);
    }

    /// <summary>
    /// Resolves a compensate intermediate throw event. A ref-less throw compensates everything; an
    /// <c>activityRef</c> is kept only when it names an element that actually carries a compensation boundary,
    /// otherwise the throw is dropped and its flows cascade-drop.
    /// </summary>
    private static BpmnElement? ResolveCompensateThrow(
        XElement element,
        IReadOnlySet<string> readElementIds,
        IReadOnlySet<string> compensationHostIds,
        ReadContext context)
    {
        var id = IdOf(element)!;
        var definitions = element.Elements().Where(IsEventDefinition).ToArray();
        if (definitions.Length != 1 || definitions[0].Name.LocalName != "compensateEventDefinition")
        {
            context.Report(BpmnImportIssueSeverity.Dropped, $"Intermediate throw event '{id}' does not declare exactly one compensate event definition; only compensate throw events are supported here, so it was dropped.", id);
            return null;
        }

        var activityRef = ((string?)definitions[0].Attribute("activityRef"))?.Trim();
        if (activityRef is { Length: > 0 } && !(readElementIds.Contains(activityRef) && compensationHostIds.Contains(activityRef)))
        {
            context.Report(BpmnImportIssueSeverity.Dropped, $"Compensate throw event '{id}' targets activityRef '{activityRef}', which is not an element with an attached compensation boundary; it was dropped.", id);
            return null;
        }

        return new BpmnElement(id, BpmnElementTypes.IntermediateThrowEvent, name: NameOf(element),
            eventDefinitions: [new BpmnEventDefinition(BpmnEventDefinitionTypes.Compensation, CompensationProperties(activityRef))]);
    }

    /// <summary>
    /// Resolves a compensate end event. A ref-less end compensates everything; an unresolvable
    /// <c>activityRef</c> degrades the element to a plain end event rather than dropping it, because an end
    /// event has no outgoing flows to cascade.
    /// </summary>
    private static BpmnElement ResolveCompensateEnd(
        XElement element,
        IReadOnlySet<string> readElementIds,
        IReadOnlySet<string> compensationHostIds,
        ReadContext context)
    {
        var id = IdOf(element)!;
        var activityRef = ((string?)element.Element(BpmnXmlNames.Model + "compensateEventDefinition")?.Attribute("activityRef"))?.Trim();
        if (activityRef is { Length: > 0 } && !(readElementIds.Contains(activityRef) && compensationHostIds.Contains(activityRef)))
        {
            context.Report(BpmnImportIssueSeverity.Degraded, $"Compensate end event '{id}' targets activityRef '{activityRef}', which is not an element with an attached compensation boundary; it read as a plain end event.", id);
            return new BpmnElement(id, BpmnElementTypes.EndEvent, name: NameOf(element));
        }

        return new BpmnElement(id, BpmnElementTypes.EndEvent, name: NameOf(element),
            eventDefinitions: [new BpmnEventDefinition(BpmnEventDefinitionTypes.Compensation, CompensationProperties(activityRef))]);
    }

    private static IReadOnlyDictionary<string, string>? CompensationProperties(string? activityRef) =>
        string.IsNullOrWhiteSpace(activityRef)
            ? null
            : new Dictionary<string, string> { [BpmnEventDefinitionProperties.ActivityRef] = activityRef.Trim() };

    private static bool IsForCompensationOf(XElement element) =>
        (bool?)element.Attribute("isForCompensation") ?? false;

    // ---------------------------------------------------------------------------------------------------
    // Activities
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Reads a <c>&lt;callActivity&gt;</c>. The element always binds a call: BPMN's <c>calledElement</c> names
    /// the process when the document supplies one, and the host resolves it. A call activity waits for the
    /// called process unless the vendor attribute <c>vw:waitForCompletion="false"</c> says otherwise, which is
    /// the only way to express a fire-and-forget call.
    /// </summary>
    private static void ReadCallActivity(
        XElement element,
        string id,
        string processId,
        IReadOnlySet<string> declaredVariableNames,
        List<BpmnElement> elements,
        ReadContext context)
    {
        var calledElement = ((string?)element.Attribute("calledElement"))?.Trim();
        var waitForCompletion = !string.Equals((string?)element.Attribute(BpmnXmlNames.Vendor + "waitForCompletion"), "false", StringComparison.OrdinalIgnoreCase);

        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(calledElement))
            properties[CalledElementPropertyKey] = calledElement;
        else
            context.Report(BpmnImportIssueSeverity.Info, $"Call activity '{id}' names no calledElement; the host must decide which process it calls.", id);

        var bindingRef = context.Bind(new BpmnWorkBinding.CallProcess(processId, id, context.BindingRefFor(id), BpmnBindingSlot.Primary,
            string.IsNullOrWhiteSpace(calledElement) ? null : calledElement, waitForCompletion));

        elements.Add(new BpmnElement(id, BpmnElementTypes.CallActivity, name: NameOf(element), bindingRef: bindingRef, defaultFlowId: DefaultOf(element),
            properties: properties.Count > 0 ? properties : null,
            loopCharacteristics: ResolveLoopCharacteristics(element, id, isActivity: true, declaredVariableNames, context)));
    }

    /// <summary>
    /// Resolves an activity's <c>&lt;multiInstanceLoopCharacteristics&gt;</c>: <c>isSequential</c> plus either
    /// an integer <c>&lt;loopCardinality&gt;</c> or the vendor <c>vw:collection</c> and
    /// <c>vw:itemVariable</c> attributes, which is how a collection is named because BPMN's own data-input
    /// form has no neutral representation here. A standard (while/until) loop, a non-integer cardinality, an
    /// undeclared collection variable, a reserved item variable, or loop characteristics on something that is
    /// not an activity all degrade: the element reads without loop characteristics.
    /// </summary>
    private static BpmnLoopCharacteristics? ResolveLoopCharacteristics(XElement element, string id, bool isActivity, IReadOnlySet<string> declaredVariableNames, ReadContext context)
    {
        if (element.Element(BpmnXmlNames.Model + "multiInstanceLoopCharacteristics") is not { } loop)
        {
            if (element.Element(BpmnXmlNames.Model + "standardLoopCharacteristics") is not null)
                context.Report(BpmnImportIssueSeverity.Degraded, $"Element '{id}' declares standardLoopCharacteristics, which this model does not represent; it read without loop characteristics.", id);
            return null;
        }

        if (!isActivity)
        {
            context.Report(BpmnImportIssueSeverity.Degraded, $"Element '{id}' declares multi-instance loop characteristics but is not an activity, which is the only thing BPMN allows them on; it read without loop characteristics.", id);
            return null;
        }

        var isSequential = (bool?)loop.Attribute("isSequential") ?? false;

        if (((string?)loop.Attribute(BpmnXmlNames.Vendor + "collection"))?.Trim() is { Length: > 0 } collection)
        {
            if (!declaredVariableNames.Contains(collection))
            {
                context.Report(BpmnImportIssueSeverity.Degraded, $"Element '{id}' declares a collection multi-instance over '{collection}', which is not a declared variable of its container; it read without loop characteristics.", id);
                return null;
            }

            var itemVariable = ((string?)loop.Attribute(BpmnXmlNames.Vendor + "itemVariable"))?.Trim() is { Length: > 0 } authoredItem
                ? authoredItem
                : BpmnLoopCharacteristics.DefaultItemVariable;
            if (StringComparer.Ordinal.Equals(itemVariable, BpmnLoopCharacteristics.LoopIndexVariable))
            {
                context.Report(BpmnImportIssueSeverity.Degraded, $"Element '{id}' declares a collection multi-instance whose item variable is the reserved '{BpmnLoopCharacteristics.LoopIndexVariable}' key; it read without loop characteristics.", id);
                return null;
            }

            return new BpmnLoopCharacteristics(isSequential: isSequential, collectionVariable: collection, itemVariable: itemVariable);
        }

        var cardinalityText = loop.Element(BpmnXmlNames.Model + "loopCardinality")?.Value.Trim();
        if (string.IsNullOrWhiteSpace(cardinalityText) ||
            !int.TryParse(cardinalityText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cardinality) ||
            cardinality < 1)
        {
            context.Report(BpmnImportIssueSeverity.Degraded, $"Element '{id}' declares multi-instance loop characteristics without a positive integer <loopCardinality>; only a literal cardinality is representable, so it read without loop characteristics.", id);
            return null;
        }

        return new BpmnLoopCharacteristics(isSequential: isSequential, cardinality: cardinality);
    }

    // ---------------------------------------------------------------------------------------------------
    // Small helpers
    // ---------------------------------------------------------------------------------------------------

    /// <summary>Resolves a message or signal name through the root declaration index; <c>null</c> when the ref is missing, unresolvable, or names a blank declaration.</summary>
    private static string? ResolveMessageSignalName(XElement definition, string type, IReadOnlyDictionary<string, string> messageSignalNames)
    {
        var refAttribute = type == BpmnEventDefinitionTypes.Message ? "messageRef" : "signalRef";
        if ((string?)definition.Attribute(refAttribute) is not { } reference)
            return null;
        if (!messageSignalNames.TryGetValue(reference.Trim(), out var name) || string.IsNullOrWhiteSpace(name))
            return null;
        return name.Trim();
    }

    /// <summary>
    /// Maps a start timer's <c>&lt;timeCycle&gt;</c> to a recurring interval or cron schedule: text beginning
    /// with <c>P</c> or <c>R</c> is an ISO-8601 duration, with any repetition prefix stripped; anything else is
    /// a cron expression. A non-recurring start returns <c>null</c>.
    /// </summary>
    private static IReadOnlyDictionary<string, string>? ResolveStartTimerProperties(XElement definition)
    {
        if (definition.Element(BpmnXmlNames.Model + "timeCycle") is not { } timeCycle)
            return null;

        var text = timeCycle.Value.Trim();
        if (text.Length == 0)
            return null;

        if (text[0] is 'P' or 'R')
        {
            var interval = StripRepetitionPrefix(text);
            return interval.Length == 0
                ? null
                : new Dictionary<string, string> { [BpmnEventDefinitionProperties.Interval] = interval };
        }

        return new Dictionary<string, string> { [BpmnEventDefinitionProperties.Cron] = text };
    }

    /// <summary>Maps a catch timer's <c>&lt;timeDuration&gt;</c> to its ISO-8601 duration; <c>null</c> for a cycle or date timer.</summary>
    private static string? ResolveCatchTimerDuration(XElement definition)
    {
        if (definition.Element(BpmnXmlNames.Model + "timeDuration") is not { } timeDuration)
            return null;

        var text = timeDuration.Value.Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>Strips an ISO-8601 repetition prefix (<c>R.../</c>) from a recurring cycle, leaving the bare duration.</summary>
    private static string StripRepetitionPrefix(string text)
    {
        if (text[0] != 'R')
            return text;
        var slash = text.IndexOf('/');
        return slash >= 0 ? text[(slash + 1)..].Trim() : text;
    }

    /// <summary>The message name an element carries: a message event definition's resolved name, or a send/receive task's recorded name.</summary>
    private static string? MessageNameOf(BpmnElement element)
    {
        if (element.EventDefinitions.FirstOrDefault(definition => StringComparer.Ordinal.Equals(definition.Type, BpmnEventDefinitionTypes.Message)) is { } messageDefinition
            && messageDefinition.Properties.TryGetValue(BpmnEventDefinitionProperties.Name, out var eventName) && !string.IsNullOrWhiteSpace(eventName))
            return eventName.Trim();
        if (element.Properties.TryGetValue(MessageNamePropertyKey, out var taskName) && !string.IsNullOrWhiteSpace(taskName))
            return taskName.Trim();
        return null;
    }

    /// <summary>Copies an element, overriding only what is named. The model's element is immutable, so a late-resolved fact means a new instance.</summary>
    private static BpmnElement CopyElement(BpmnElement element, string? laneId = null, bool? isForCompensation = null, BpmnExtensions? extensions = null) =>
        new(element.ElementId, element.ElementType, element.Name, element.BindingRef, laneId ?? element.LaneId,
            element.DefaultFlowId, element.EventDefinitions, element.Properties, element.AttachedToRef, element.CancelActivity,
            element.LoopCharacteristics, isForCompensation ?? element.IsForCompensation, element.CompensationHandlerElementId,
            element.IsTransaction, element.TriggeredByEvent, element.ListenerBindingRef, extensions ?? element.Extensions);

    /// <summary>The activities BPMN lets a boundary event attach to, and therefore also the activities that can act as a compensation handler.</summary>
    private static bool IsBoundaryHost(string elementType) =>
        BpmnXmlNames.TaskLocalNamesToElementTypes.Values.Contains(elementType, StringComparer.Ordinal)
        || StringComparer.Ordinal.Equals(elementType, BpmnElementTypes.SubProcess)
        || StringComparer.Ordinal.Equals(elementType, BpmnElementTypes.CallActivity);

    private static bool IsEventDefinition(XElement element) =>
        element.Name.Namespace == BpmnXmlNames.Model && element.Name.LocalName.EndsWith("EventDefinition", StringComparison.Ordinal);

    private static string? IdOf(XElement element) => (string?)element.Attribute("id");
    private static string? NameOf(XElement element) => (string?)element.Attribute("name");
    private static string? DefaultOf(XElement element) => (string?)element.Attribute("default");

    private static string Named(string? name) => string.IsNullOrWhiteSpace(name) ? "" : $" ('{name.Trim()}')";
    private static string Quote(string? value) => value is null ? "(none)" : $"'{value}'";
    private static string Capitalize(string value) => value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];

    // ---------------------------------------------------------------------------------------------------
    // Retention
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Flow nodes whose own foreign content is retained onto <see cref="BpmnElement.Extensions"/>.
    /// Subprocesses are excluded: their content is the nested process, which retains its own. Sequence flows
    /// are excluded because they are constructed once, in one place, and take their retained content directly.
    /// </summary>
    private static bool IsRetainableFlowNode(string localName) =>
        localName is "startEvent" or "endEvent" or "intermediateCatchEvent" or "intermediateThrowEvent"
            or "callActivity" or "boundaryEvent"
        || BpmnXmlNames.TaskLocalNamesToElementTypes.ContainsKey(localName)
        || BpmnXmlNames.GatewayLocalNamesToElementTypes.ContainsKey(localName);


    private static bool IsFlowNodeChildConsumed(XElement child) =>
        child.Name.Namespace == BpmnXmlNames.Model
        && (child.Name.LocalName is "incoming" or "outgoing" or "conditionExpression"
                or "multiInstanceLoopCharacteristics" or "standardLoopCharacteristics"
            || IsEventDefinition(child));

    private static bool IsContainerChildConsumed(XElement child) =>
        child.Name.Namespace == BpmnXmlNames.Model
        && (child.Name.LocalName is "startEvent" or "endEvent" or "intermediateCatchEvent" or "intermediateThrowEvent"
                or "subProcess" or "transaction" or "callActivity" or "boundaryEvent" or "sequenceFlow"
                or "laneSet" or "association" or "incoming" or "outgoing"
            || BpmnXmlNames.TaskLocalNamesToElementTypes.ContainsKey(child.Name.LocalName)
            || BpmnXmlNames.GatewayLocalNamesToElementTypes.ContainsKey(child.Name.LocalName));

    private static bool IsDefinitionsChildConsumed(XElement child) =>
        (child.Name.Namespace == BpmnXmlNames.Model
         && child.Name.LocalName is "process" or "collaboration" or "message" or "signal" or "error" or "escalation")
        || child.Name == BpmnXmlNames.Di + "BPMNDiagram";

    private static bool IsCollaborationChildConsumed(XElement child) =>
        child.Name.Namespace == BpmnXmlNames.Model
        && child.Name.LocalName is "participant" or "messageFlow";

    // ---------------------------------------------------------------------------------------------------
    // Read-time state
    // ---------------------------------------------------------------------------------------------------

    /// <summary>A collaboration participant: its id, optional name, and referenced process id (null for a black-box pool).</summary>
    private readonly record struct ParticipantDeclaration(string Id, string? Name, string? ProcessRef);

    /// <summary>A collaboration message flow as authored: its id, optional name, endpoint refs, and optional messageRef.</summary>
    private readonly record struct MessageFlowDeclaration(string FlowId, string? Name, string SourceRef, string TargetRef, string? MessageRef);

    private enum MessageFlowEndpointKind { Unresolvable, Element, BlackBox }

    private readonly record struct MessageFlowEndpoint(MessageFlowEndpointKind Kind, string? ElementId, string? PoolId, string? MessageName)
    {
        public static readonly MessageFlowEndpoint Unresolvable = new(MessageFlowEndpointKind.Unresolvable, null, null, null);
        public static MessageFlowEndpoint Element(string elementId, string? poolId, string? messageName) => new(MessageFlowEndpointKind.Element, elementId, poolId, messageName);
        public static MessageFlowEndpoint BlackBox(string poolId) => new(MessageFlowEndpointKind.BlackBox, null, poolId, null);
    }

    /// <summary>One process as read, plus the collaboration facts folded onto it afterwards.</summary>
    private sealed class BuiltProcess(string processId, BpmnProcessDefinition definition, bool isExecutable)
    {
        public string ProcessId { get; } = processId;
        public BpmnProcessDefinition Definition { get; set; } = definition;
        public bool IsExecutable { get; } = isExecutable;
        public List<ParticipantDeclaration> ReferencingParticipants { get; } = [];
        public string? LanePoolId { get; set; }
    }

    /// <summary>Everything a single read accumulates: findings, the element histogram, the bindings, and retained content.</summary>
    private sealed class ReadContext(BpmnImportOptions? options)
    {
        private readonly string _bindingRefPrefix = string.IsNullOrWhiteSpace(options?.BindingRefPrefix) ? DefaultBindingRefPrefix : options!.BindingRefPrefix!.Trim();
        private readonly Dictionary<string, int> _elementCounts = new(StringComparer.Ordinal);

        public BpmnFidelity Fidelity { get; } = options?.Fidelity ?? BpmnFidelity.Lossless;
        public List<string> ProcessIds { get; } = [];
        public List<BpmnImportIssue> Issues { get; } = [];
        public List<BpmnWorkBinding> Bindings { get; } = [];
        public Dictionary<string, string> LaneByElementId { get; } = new(StringComparer.Ordinal);
        public RetentionLog Retention { get; } = new();
        public string? CurrentProcessId { get; set; }

        public string BindingRefFor(string elementId) => $"{_bindingRefPrefix}-{elementId}";

        public string Bind(BpmnWorkBinding binding)
        {
            Bindings.Add(binding);
            return binding.BindingRef;
        }

        public void DropBindingsFrom(int mark) => Bindings.RemoveRange(mark, Bindings.Count - mark);

        /// <summary>Removes an element's own binding and, for a subprocess, everything its nested process bound.</summary>
        public void DropBindingsFor(BpmnElement element) =>
            Bindings.RemoveAll(binding =>
                StringComparer.Ordinal.Equals(binding.ElementId, element.ElementId)
                || StringComparer.Ordinal.Equals(binding.ProcessId, element.ElementId));

        public void Report(BpmnImportIssueSeverity severity, string message, string? elementId = null, string? processId = null) =>
            Issues.Add(new BpmnImportIssue(severity, message, elementId, processId ?? CurrentProcessId));

        public void CountElement(string localName) =>
            _elementCounts[localName] = (_elementCounts.TryGetValue(localName, out var count) ? count : 0) + 1;

        public BpmnImportAnalysis ToAnalysis() => new(ProcessIds, _elementCounts, Issues);
    }
}
