using Bpmn.Model;
using Bpmn.Semantics.Behaviors;

namespace Bpmn.Semantics;

/// <summary>
/// The validated executable BPMN graph: elements, sequence flows, work bindings, and family resolution.
/// Construction validates the structural invariants (unique ids, resolvable references, at least one start
/// event, event and gateway binding rules, a single default flow per element) so the interpreter can navigate
/// without re-checking, and precomputes the backward (loop-back) flow set so the interpreter can mint
/// loop-iteration keys. Cyclic graphs are executable; the structural rules still forbid a loop-back into a
/// start event, a boundary event, or an event-gateway-armed catch, and a cycle on which nothing can hold a token
/// is refused, because a token that entered it would circulate forever without the evaluation returning.
/// </summary>
public sealed class BpmnGraph
{
    private readonly IReadOnlyDictionary<string, BpmnElement> _elementsById;
    private readonly IReadOnlyDictionary<string, BpmnSequenceFlow> _flowsById;
    private readonly IReadOnlyDictionary<string, BpmnBoundWork> _boundWorkByRef;
    private readonly IReadOnlyDictionary<string, BpmnElement> _elementsByBindingRef;
    private readonly ILookup<string, BpmnSequenceFlow> _outboundBySource;
    private readonly ILookup<string, BpmnSequenceFlow> _inboundByTarget;
    private readonly ILookup<string, BpmnElement> _boundariesByHost;
    private readonly IReadOnlySet<string> _backwardFlowIds;
    private readonly IReadOnlySet<string> _linkFlowIds;
    private readonly IReadOnlyList<BpmnEventSubprocessCatcher> _eventSubprocesses;

    private BpmnGraph(
        BpmnProcessDefinition definition,
        IReadOnlyCollection<BpmnElement> elements,
        IReadOnlyCollection<BpmnSequenceFlow> sequenceFlows,
        IReadOnlyCollection<BpmnSequenceFlow> linkFlows,
        IReadOnlyDictionary<string, BpmnBoundWork> boundWorkByRef,
        bool isTransaction,
        IReadOnlyList<BpmnEventSubprocessCatcher> eventSubprocesses)
    {
        Definition = definition;
        Elements = elements;
        SequenceFlows = sequenceFlows;
        IsTransaction = isTransaction;
        _boundWorkByRef = boundWorkByRef;
        _eventSubprocesses = eventSubprocesses;
        _elementsById = elements.ToDictionary(element => element.ElementId, StringComparer.Ordinal);

        // A link throw reaches its catch along a synthesized flow, so every rule that reasons about flows (routing,
        // reachability, loop-back detection, the cycle-that-cannot-wait check) sees the jump as the edge it is.
        var routingFlows = sequenceFlows.Concat(linkFlows).ToArray();
        _linkFlowIds = linkFlows.Select(flow => flow.FlowId).ToHashSet(StringComparer.Ordinal);
        _flowsById = routingFlows.ToDictionary(flow => flow.FlowId, StringComparer.Ordinal);
        _elementsByBindingRef = elements
            .Where(element => element.BindingRef is not null)
            .ToDictionary(element => element.BindingRef!, StringComparer.Ordinal);
        _outboundBySource = routingFlows.ToLookup(flow => flow.SourceRef, StringComparer.Ordinal);
        _inboundByTarget = routingFlows.ToLookup(flow => flow.TargetRef, StringComparer.Ordinal);
        _boundariesByHost = elements
            .Where(element => StringComparer.Ordinal.Equals(element.ElementType, BpmnElementTypes.BoundaryEvent) && element.AttachedToRef is not null)
            .ToLookup(element => element.AttachedToRef!, StringComparer.Ordinal);
        StartEvents = elements
            .Where(element => StringComparer.Ordinal.Equals(element.ElementType, BpmnElementTypes.StartEvent))
            .ToArray();
        _backwardFlowIds = ComputeBackwardFlowIds(elements, _outboundBySource, StartEvents);
    }

    /// <summary>Every flow element in the process.</summary>
    /// <summary>
    /// The definition this graph was built from.
    /// <para>
    /// A graph depends only on the definition, the bound work and the host capabilities, so hosts are
    /// encouraged to build one per definition and reuse it across instances. Keeping the source here means
    /// doing that does not cost the process identity a host needs for reporting.
    /// </para>
    /// </summary>
    public BpmnProcessDefinition Definition { get; }

    /// <summary>The BPMN id of the process this graph represents.</summary>
    public string ProcessId => Definition.ProcessId;

    public IReadOnlyCollection<BpmnElement> Elements { get; }

    /// <summary>
    /// Every sequence flow the definition declares. The synthesized link flows (see <see cref="IsLinkFlow"/>) are not
    /// among them, although <see cref="OutboundFlows"/>, <see cref="InboundFlows"/> and <see cref="GetRequiredFlow"/>
    /// include them.
    /// </summary>
    public IReadOnlyCollection<BpmnSequenceFlow> SequenceFlows { get; }

    /// <summary>The process's start events.</summary>
    public IReadOnlyCollection<BpmnElement> StartEvents { get; }

    /// <summary>Whether this process is a transaction: its scope may be cancelled from within by a cancel end event.</summary>
    public bool IsTransaction { get; }

    /// <summary>The loop-closing (backward) sequence flow ids; a token that traverses one mints a fresh iteration key.</summary>
    public IReadOnlyCollection<string> BackwardFlowIds => _backwardFlowIds;

    /// <summary>
    /// Validates <paramref name="definition"/> against <paramref name="boundWork"/> and returns the executable
    /// graph. Throws <see cref="BpmnExecutionException"/> for a structurally invalid definition, including one with
    /// a cycle that can never wait, and <see cref="BpmnCapabilityException"/> when the definition needs a host
    /// capability <paramref name="capabilities"/> does not declare.
    /// </summary>
    /// <param name="definition">The process to execute.</param>
    /// <param name="boundWork">
    /// The work the host has bound, one entry per <see cref="BpmnElement.BindingRef"/> and
    /// <see cref="BpmnElement.ListenerBindingRef"/> the definition declares. Every declared binding must be
    /// present and every entry must be bound by exactly one element.
    /// </param>
    /// <param name="capabilities">What the host running this graph can do.</param>
    public static BpmnGraph Build(
        BpmnProcessDefinition definition,
        IReadOnlyCollection<BpmnBoundWork> boundWork,
        BpmnHostCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(boundWork);

        BpmnCapabilityRequirements.Analyze(definition).ThrowIfUnmet(capabilities, definition.ProcessId);

        var boundWorkByRef = new Dictionary<string, BpmnBoundWork>(StringComparer.Ordinal);
        foreach (var work in boundWork)
        {
            if (!boundWorkByRef.TryAdd(work.BindingRef, work))
                throw new BpmnExecutionException($"BPMN bound work '{work.BindingRef}' is supplied more than once.");
        }

        var elements = definition.Elements;
        var flows = definition.SequenceFlows;
        var variableNames = definition.Variables.Select(variable => variable.Name).ToHashSet(StringComparer.Ordinal);

        var eventSubprocesses = Validate(elements, flows, boundWorkByRef, variableNames, definition.IsTransaction);
        var linkFlows = LinkFlows(elements, flows);

        var graph = new BpmnGraph(definition, elements, flows, linkFlows, boundWorkByRef, definition.IsTransaction, eventSubprocesses);
        graph.RefuseCyclesThatCannotWait();
        return graph;
    }

    /// <summary>The element with this id, or throws when the process has none.</summary>
    public BpmnElement GetRequiredElement(string elementId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(elementId);

        if (_elementsById.TryGetValue(elementId, out var element))
            return element;

        throw new BpmnExecutionException($"BPMN element '{elementId}' does not exist in the process graph.") { ElementIds = [elementId] };
    }

    /// <summary>The sequence flow with this id, or throws when the process has none.</summary>
    public BpmnSequenceFlow GetRequiredFlow(string flowId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flowId);

        if (_flowsById.TryGetValue(flowId, out var flow))
            return flow;

        throw new BpmnExecutionException($"BPMN sequence flow '{flowId}' does not exist in the process graph.") { ElementIds = [flowId] };
    }

    /// <summary>The element whose <see cref="BpmnElement.BindingRef"/> is <paramref name="bindingRef"/>, or <c>null</c>.</summary>
    public BpmnElement? FindElementByBindingRef(string bindingRef) =>
        _elementsByBindingRef.TryGetValue(bindingRef, out var element) ? element : null;

    /// <summary>The work bound to <paramref name="bindingRef"/>, or throws when nothing is bound to it.</summary>
    public BpmnBoundWork GetRequiredBoundWork(string bindingRef)
    {
        if (_boundWorkByRef.TryGetValue(bindingRef, out var work))
            return work;

        throw new BpmnExecutionException($"BPMN binding ref '{bindingRef}' names no bound work.");
    }

    /// <summary>The sequence flows leaving <paramref name="elementId"/>.</summary>
    public IReadOnlyCollection<BpmnSequenceFlow> OutboundFlows(string elementId) =>
        _outboundBySource[elementId].ToArray();

    /// <summary>The sequence flows arriving at <paramref name="elementId"/>.</summary>
    public IReadOnlyCollection<BpmnSequenceFlow> InboundFlows(string elementId) =>
        _inboundByTarget[elementId].ToArray();

    /// <summary>The timer/message/signal catch boundary events attached to <paramref name="hostElementId"/>; these arm suspending listener work when the host starts. Error, escalation, compensation, and cancel boundaries arm nothing.</summary>
    public IReadOnlyCollection<BpmnElement> AttachedCatchBoundaries(string hostElementId) =>
        _boundariesByHost[hostElementId]
            .Where(boundary => !BpmnElementFamilies.IsErrorBoundary(boundary) && !BpmnElementFamilies.IsEscalationBoundary(boundary) && !BpmnElementFamilies.IsCompensationBoundary(boundary) && !BpmnElementFamilies.IsCancelBoundary(boundary))
            .ToArray();

    /// <summary>The escalation boundary events attached to <paramref name="hostElementId"/>, in element order; dormant and notification-driven. A host may carry several with distinct codes plus at most one code-less catch-all.</summary>
    public IReadOnlyCollection<BpmnElement> AttachedEscalationBoundaries(string hostElementId) =>
        _boundariesByHost[hostElementId]
            .Where(BpmnElementFamilies.IsEscalationBoundary)
            .ToArray();

    /// <summary>The error boundary events attached to <paramref name="hostElementId"/>, in element order; dormant and fault-driven. A host may carry several with distinct codes plus at most one code-less catch-all.</summary>
    public IReadOnlyCollection<BpmnElement> AttachedErrorBoundaries(string hostElementId) =>
        _boundariesByHost[hostElementId]
            .Where(BpmnElementFamilies.IsErrorBoundary)
            .ToArray();

    /// <summary>
    /// The error boundary on <paramref name="hostElementId"/> that catches an error coded <paramref name="errorCode"/>:
    /// the boundary declaring exactly that code wins; else the code-less catch-all; else <c>null</c>. An error with
    /// no code (<c>null</c>) reaches only the catch-all.
    /// </summary>
    public BpmnElement? AttachedErrorBoundary(string hostElementId, string? errorCode)
    {
        var boundaries = AttachedErrorBoundaries(hostElementId);
        return (errorCode is null ? null : boundaries.FirstOrDefault(boundary => StringComparer.Ordinal.Equals(ReadErrorCode(boundary), errorCode)))
               ?? boundaries.FirstOrDefault(boundary => ReadErrorCode(boundary) is null);
    }

    /// <summary>
    /// The code-less catch-all error boundary attached to <paramref name="hostElementId"/>, which is the only one an
    /// error with no code reaches, or <c>null</c>. A host used to carry at most one error boundary, which caught any
    /// error; since error codes are matched (ADR 0012) a host may carry several, so "the" error boundary no longer
    /// exists.
    /// </summary>
    [Obsolete("A host may carry several error boundaries since error codes are matched (ADR 0012). Use AttachedErrorBoundary(hostElementId, errorCode) or AttachedErrorBoundaries(hostElementId).")]
    public BpmnElement? AttachedErrorBoundary(string hostElementId) => AttachedErrorBoundary(hostElementId, errorCode: null);

    /// <summary>The single compensation boundary attached to <paramref name="hostElementId"/>, or <c>null</c> when the host has none; validation caps a host at one compensation boundary.</summary>
    public BpmnElement? AttachedCompensationBoundary(string hostElementId) =>
        _boundariesByHost[hostElementId].FirstOrDefault(BpmnElementFamilies.IsCompensationBoundary);

    /// <summary>The single cancel boundary attached to <paramref name="hostElementId"/>, or <c>null</c> when the transaction host has none; validation caps a transaction host at one cancel boundary.</summary>
    public BpmnElement? AttachedCancelBoundary(string hostElementId) =>
        _boundariesByHost[hostElementId].FirstOrDefault(BpmnElementFamilies.IsCancelBoundary);

    /// <summary>The event subprocesses of this scope: flow-less <c>TriggeredByEvent</c> subprocesses indexed by their body start-event trigger (kind + code), graph-derived.</summary>
    public IReadOnlyList<BpmnEventSubprocessCatcher> EventSubprocesses => _eventSubprocesses;

    /// <summary>The scope's external-trigger (message/signal/timer) event subprocesses, in deterministic element-id ordinal order; each is armed as a scope listener at scope start.</summary>
    public IReadOnlyList<BpmnEventSubprocessCatcher> ExternalTriggerEventSubprocesses =>
        _eventSubprocesses
            .Where(catcher => catcher.IsExternalTrigger)
            .OrderBy(catcher => catcher.ElementId, StringComparer.Ordinal)
            .ToArray();

    /// <summary>The event subprocess bound to <paramref name="elementId"/>, or <c>null</c>; each <c>TriggeredByEvent</c> element is exactly one catcher.</summary>
    public BpmnEventSubprocessCatcher? EventSubprocessByElementId(string elementId) =>
        _eventSubprocesses.FirstOrDefault(catcher => StringComparer.Ordinal.Equals(catcher.ElementId, elementId));

    /// <summary>The escalation event subprocess matching <paramref name="escalationCode"/>: an exact code match wins; else the code-less catch-all; else <c>null</c>.</summary>
    public BpmnEventSubprocessCatcher? EscalationEventSubprocess(string escalationCode) =>
        EscalationEventSubprocessExact(escalationCode) ?? EscalationCatchAllEventSubprocess();

    /// <summary>The escalation event subprocess with the exact code <paramref name="escalationCode"/>, or <c>null</c>.</summary>
    public BpmnEventSubprocessCatcher? EscalationEventSubprocessExact(string escalationCode) =>
        _eventSubprocesses.FirstOrDefault(catcher =>
            catcher.TriggerKind == BpmnEventSubprocessTriggerKind.Escalation &&
            catcher.Code is not null && StringComparer.Ordinal.Equals(catcher.Code, escalationCode));

    /// <summary>The code-less catch-all escalation event subprocess, or <c>null</c>.</summary>
    public BpmnEventSubprocessCatcher? EscalationCatchAllEventSubprocess() =>
        _eventSubprocesses.FirstOrDefault(catcher =>
            catcher.TriggerKind == BpmnEventSubprocessTriggerKind.Escalation && catcher.Code is null);

    /// <summary>
    /// The error event subprocess of this scope that catches an error coded <paramref name="errorCode"/>: an exact
    /// code match wins; else the code-less catch-all; else <c>null</c>. An error with no code (<c>null</c>) reaches
    /// only the catch-all.
    /// </summary>
    public BpmnEventSubprocessCatcher? ErrorEventSubprocess(string? errorCode) =>
        (errorCode is null ? null : ErrorEventSubprocessExact(errorCode)) ?? ErrorCatchAllEventSubprocess();

    /// <summary>The error event subprocess with the exact code <paramref name="errorCode"/>, or <c>null</c>.</summary>
    public BpmnEventSubprocessCatcher? ErrorEventSubprocessExact(string errorCode) =>
        _eventSubprocesses.FirstOrDefault(catcher =>
            catcher.TriggerKind == BpmnEventSubprocessTriggerKind.Error &&
            catcher.Code is not null && StringComparer.Ordinal.Equals(catcher.Code, errorCode));

    /// <summary>The code-less catch-all error event subprocess, or <c>null</c>.</summary>
    public BpmnEventSubprocessCatcher? ErrorCatchAllEventSubprocess() =>
        _eventSubprocesses.FirstOrDefault(catcher =>
            catcher.TriggerKind == BpmnEventSubprocessTriggerKind.Error && catcher.Code is null);

    /// <summary>
    /// The code-less catch-all error event subprocess, which is the only one an error with no code reaches, or
    /// <c>null</c>. A scope used to carry at most one error event subprocess, which caught any error; since error
    /// codes are matched (ADR 0012) a scope may carry several, so "the" error event subprocess no longer exists.
    /// </summary>
    [Obsolete("A scope may carry several error event subprocesses since error codes are matched (ADR 0012). Use ErrorEventSubprocess(errorCode).")]
    public BpmnEventSubprocessCatcher? ErrorEventSubprocess() => ErrorCatchAllEventSubprocess();

    /// <summary>The element's default sequence flow, or <c>null</c> when it declares none.</summary>
    public BpmnSequenceFlow? GetDefaultFlow(BpmnElement element)
    {
        if (element.DefaultFlowId is not null)
            return GetRequiredFlow(element.DefaultFlowId);

        return _outboundBySource[element.ElementId].FirstOrDefault(flow => flow.IsDefault);
    }

    /// <summary>True when any path over sequence flows leads from <paramref name="sourceElementId"/> to <paramref name="targetElementId"/>.</summary>
    public bool CanReach(string sourceElementId, string targetElementId)
    {
        if (StringComparer.Ordinal.Equals(sourceElementId, targetElementId))
            return true;

        var visited = new HashSet<string>(StringComparer.Ordinal) { sourceElementId };
        var queue = new Queue<string>();
        queue.Enqueue(sourceElementId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var next in _outboundBySource[current].Select(flow => flow.TargetRef))
            {
                if (StringComparer.Ordinal.Equals(next, targetElementId))
                    return true;

                if (visited.Add(next))
                    queue.Enqueue(next);
            }
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="flowId"/> is a backward (loop-back) sequence flow: a token that
    /// traverses it mints a fresh loop-iteration key rather than inheriting the emitting token's. The set is
    /// precomputed at construction (<see cref="ComputeBackwardFlowIds"/>) and is a deterministic function of
    /// the element/flow/start-event sets.
    /// </summary>
    public bool IsBackwardFlow(string flowId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flowId);
        return _backwardFlowIds.Contains(flowId);
    }

    /// <summary>
    /// Whether <paramref name="flowId"/> is a synthesized link flow: the edge from a link throw to the link catch of the
    /// same name in this scope, with the id <c>link:</c> followed by the throw's element id. A token that takes a
    /// link jump reports that id as the flow it arrived by. The definition declares no such flow.
    /// </summary>
    public bool IsLinkFlow(string flowId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flowId);
        return _linkFlowIds.Contains(flowId);
    }

    /// <summary>The id of the synthesized flow a link throw jumps along: <c>link:</c> followed by the throw's element id, which no BPMN id can equal.</summary>
    private static string LinkFlowId(string throwElementId) => $"link:{throwElementId}";

    /// <summary>
    /// Validates the scope's link events and returns one synthesized flow per link throw, from the throw to the link
    /// catch of the same name. A link pairs throws and catches within one scope only; a subprocess body is its own
    /// graph. Refuses, naming the elements and flows involved: a link event with no name or with bound work, two
    /// catches with one name, a throw with no catch of its name, a sequence flow into a catch or out of a throw, and a
    /// declared flow whose id is the one the throw's link flow needs.
    /// </summary>
    private static IReadOnlyCollection<BpmnSequenceFlow> LinkFlows(IReadOnlyCollection<BpmnElement> elements, IReadOnlyCollection<BpmnSequenceFlow> flows)
    {
        var throws = elements.Where(element => BpmnElementFamilies.Resolve(element) == BpmnElementFamilies.IntermediateThrowEventLink).ToArray();
        var catches = elements.Where(element => BpmnElementFamilies.Resolve(element) == BpmnElementFamilies.IntermediateCatchEventLink).ToArray();
        if (throws.Length == 0 && catches.Length == 0)
        {
            return [];
        }

        foreach (var link in throws.Concat(catches))
        {
            var kind = Array.IndexOf(throws, link) >= 0 ? "throw" : "catch";
            if (LinkName(link) is null)
                throw new BpmnExecutionException($"BPMN link {kind} event '{link.ElementId}' must declare a non-empty link name; the name is what pairs a throw with its catch.") { ElementIds = [link.ElementId] };
            if (link.BindingRef is not null || link.ListenerBindingRef is not null)
                throw new BpmnExecutionException($"BPMN link {kind} event '{link.ElementId}' cannot bind work; a link only moves the token to its catch.") { ElementIds = [link.ElementId] };
        }

        var catchesByName = new Dictionary<string, BpmnElement>(StringComparer.Ordinal);
        foreach (var named in catches.GroupBy(element => LinkName(element)!, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            if (named.Count() > 1)
                throw new BpmnExecutionException($"BPMN link catch events {string.Join(", ", named.Select(element => $"'{element.ElementId}'"))} share the link name '{named.Key}'; a scope may have only one catch per link name, so a throw knows where to jump.") { ElementIds = [.. named.Select(element => element.ElementId)] };
            catchesByName[named.Key] = named.Single();
        }

        foreach (var linkCatch in catches)
        {
            var inbound = flows.Where(flow => StringComparer.Ordinal.Equals(flow.TargetRef, linkCatch.ElementId)).ToArray();
            if (inbound.Length > 0)
                throw new BpmnExecutionException($"BPMN link catch event '{linkCatch.ElementId}' has incoming sequence flows {string.Join(", ", inbound.Select(flow => $"'{flow.FlowId}'"))}; a link catch is reached only by a link throw.") { ElementIds = [linkCatch.ElementId, .. inbound.Select(flow => flow.FlowId)] };
        }

        var declaredFlowIds = flows.Select(flow => flow.FlowId).ToHashSet(StringComparer.Ordinal);
        var linkFlows = new List<BpmnSequenceFlow>(throws.Length);
        foreach (var linkThrow in throws)
        {
            var outbound = flows.Where(flow => StringComparer.Ordinal.Equals(flow.SourceRef, linkThrow.ElementId)).ToArray();
            if (outbound.Length > 0)
                throw new BpmnExecutionException($"BPMN link throw event '{linkThrow.ElementId}' has outgoing sequence flows {string.Join(", ", outbound.Select(flow => $"'{flow.FlowId}'"))}; a link throw continues only at its catch.") { ElementIds = [linkThrow.ElementId, .. outbound.Select(flow => flow.FlowId)] };

            var name = LinkName(linkThrow)!;
            if (!catchesByName.TryGetValue(name, out var linkCatch))
                throw new BpmnExecutionException($"BPMN link throw event '{linkThrow.ElementId}' jumps to link '{name}', but no link catch event in its scope has that name.") { ElementIds = [linkThrow.ElementId] };

            var flowId = LinkFlowId(linkThrow.ElementId);
            if (declaredFlowIds.Contains(flowId))
                throw new BpmnExecutionException($"BPMN sequence flow id '{flowId}' is the id of link throw event '{linkThrow.ElementId}''s link flow; rename the sequence flow.") { ElementIds = [flowId, linkThrow.ElementId] };

            linkFlows.Add(new BpmnSequenceFlow(flowId, linkThrow.ElementId, linkCatch.ElementId));
        }

        return linkFlows;
    }

    /// <summary>The trimmed link name a link event's definition declares, or <c>null</c> when it declares none.</summary>
    private static string? LinkName(BpmnElement element) =>
        element.EventDefinitions.Single().Properties.TryGetValue(BpmnEventDefinitionProperties.Name, out var name) && !string.IsNullOrWhiteSpace(name)
            ? name.Trim()
            : null;

    /// <summary>
    /// Classifies the graph's backward (loop-closing) sequence flows once at construction. A
    /// backward edge is the standard compiler back edge: during a depth-first traversal from the start-event
    /// roots, an edge <c>u → v</c> is backward iff <c>v</c> is GRAY — on the current DFS stack, an ancestor of
    /// <c>u</c> — when the edge is examined. This marks exactly the loop-closing edge of each loop and never a
    /// forward/cross edge of the cycle (unlike the naive "target can reach source", which marks every edge of
    /// a cycle). Roots are the start events, then any remaining unvisited elements; both root lists and each
    /// element's outbound flows are ordinal-sorted, so the result is stable regardless of authoring order and of
    /// multi-start iteration order.
    /// </summary>
    private static IReadOnlySet<string> ComputeBackwardFlowIds(
        IReadOnlyCollection<BpmnElement> elements,
        ILookup<string, BpmnSequenceFlow> outboundBySource,
        IReadOnlyCollection<BpmnElement> startEvents)
    {
        const int gray = 1;
        const int black = 2;

        var backward = new HashSet<string>(StringComparer.Ordinal);
        var color = new Dictionary<string, int>(StringComparer.Ordinal);

        var roots = startEvents.Select(element => element.ElementId)
            .OrderBy(id => id, StringComparer.Ordinal)
            .Concat(elements.Select(element => element.ElementId).OrderBy(id => id, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal);

        foreach (var root in roots)
            Visit(root);

        return backward;

        void Visit(string elementId)
        {
            color[elementId] = gray;

            foreach (var flow in outboundBySource[elementId].OrderBy(flow => flow.FlowId, StringComparer.Ordinal))
            {
                var known = color.TryGetValue(flow.TargetRef, out var state) ? state : 0;
                if (known == gray)
                    backward.Add(flow.FlowId); // target is on the current DFS stack — a loop-closing edge.
                else if (known != black)
                    Visit(flow.TargetRef);
            }

            color[elementId] = black;
        }
    }

    /// <summary>
    /// Refuses a cycle on which no element can hold a token. A token that enters such a cycle is routed around
    /// it within one evaluation forever: the interpreter never returns to the host. The refusal names the cycle's
    /// element ids in flow order, opening at the target of its loop-back flow, the element where each pass
    /// mints its iteration key.
    /// <para>
    /// The rule is deliberately one-sided. A cycle is refused only when every element on it passes the token
    /// straight on (see <see cref="FlowsTakenWithoutWaiting"/>) along flows it is certain to take. Anything that
    /// might hold the token, depending on data or on other branches, counts as able to wait, so the cycle builds
    /// and <see cref="BpmnInterpreter.PropagationStepLimit"/> stops a run on which it did not wait. Refusing a
    /// definition that would have run is not recoverable by the host; a run stopped by the limit faults loudly.
    /// </para>
    /// <para>
    /// The cycle is judged on its own. Another branch that ends the scope first, such as a parallel path into a
    /// terminate end event, can cut such a cycle short within the same evaluation, but nothing that happens off
    /// the cycle makes the cycle itself able to wait, and a model whose termination rests on that race is refused.
    /// Nor does the check ask whether a token can reach the cycle at all: one no token can get to is refused too.
    /// </para>
    /// <para>
    /// The search runs over the pass-through elements only. Searching the whole graph and testing each cycle it
    /// happened to close would miss a pass-through cycle whose elements the search first reached through an
    /// element that waits.
    /// </para>
    /// </summary>
    private void RefuseCyclesThatCannotWait()
    {
        var passThrough = new Dictionary<string, IReadOnlyCollection<BpmnSequenceFlow>>(StringComparer.Ordinal);
        foreach (var element in Elements)
        {
            if (FlowsTakenWithoutWaiting(element) is { } taken)
                passThrough[element.ElementId] = taken;
        }

        const int gray = 1;
        const int black = 2;
        var color = new Dictionary<string, int>(StringComparer.Ordinal);
        var pathElementIds = new List<string>();
        var pathFlows = new List<BpmnSequenceFlow>();

        foreach (var root in passThrough.Keys.OrderBy(id => id, StringComparer.Ordinal))
        {
            if (!color.ContainsKey(root))
                Visit(root);
        }

        void Visit(string elementId)
        {
            color[elementId] = gray;
            pathElementIds.Add(elementId);

            foreach (var flow in passThrough[elementId].OrderBy(flow => flow.FlowId, StringComparer.Ordinal))
            {
                // A target that can hold the token ends this path: the cycle through it waits there.
                if (!passThrough.ContainsKey(flow.TargetRef))
                    continue;

                var known = color.TryGetValue(flow.TargetRef, out var state) ? state : 0;
                if (known == gray)
                    throw CycleThatCannotWait(pathFlows.Skip(pathElementIds.IndexOf(flow.TargetRef)).Append(flow).ToList(), passThrough);

                if (known != black)
                {
                    pathFlows.Add(flow);
                    Visit(flow.TargetRef);
                    pathFlows.RemoveAt(pathFlows.Count - 1);
                }
            }

            pathElementIds.RemoveAt(pathElementIds.Count - 1);
            color[elementId] = black;
        }
    }

    /// <summary>
    /// The refusal for a cycle, given its flows in order: each flow's target is the next flow's source, and the last
    /// closes back on the first. It also names every conditional flow an element on the cycle never takes. That flow
    /// is usually the way out the modeler meant, and it is not taken because the element binds nothing that could
    /// report the outcome it waits for.
    /// </summary>
    private BpmnExecutionException CycleThatCannotWait(List<BpmnSequenceFlow> cycle, IReadOnlyDictionary<string, IReadOnlyCollection<BpmnSequenceFlow>> passThrough)
    {
        // Open at the loop entry, so the message reads from where the modeler drew the loop back to. Every cycle
        // contains a backward flow; the fallback only keeps a defect in that reasoning from hiding the refusal.
        var backward = cycle.FindIndex(flow => _backwardFlowIds.Contains(flow.FlowId));
        if (backward >= 0)
            cycle = cycle.Skip(backward + 1).Concat(cycle.Take(backward + 1)).ToList();

        var elementIds = cycle.Select(flow => flow.SourceRef).ToList();
        var ignored = elementIds
            .Select(id => IgnoredConditionalFlows(GetRequiredElement(id), passThrough[id]))
            .Where(clause => clause is not null)
            .Select(clause => clause!.Value)
            .ToList();

        return new BpmnExecutionException(
            $"BPMN process '{ProcessId}' has a cycle that can never wait: {string.Join(" -> ", elementIds.Append(elementIds[0]).Select(id => $"'{id}'"))}. "
            + "Every element on it passes a token straight on in the same evaluation, so a token that enters it circulates forever and the evaluation never returns. "
            + string.Concat(ignored.Select(clause => clause.Sentence + " "))
            + "Put something on the cycle that waits, such as an element that binds work (a task, a catch event, a decision gateway), or route out of it.")
        {
            ElementIds = [.. elementIds, .. ignored.SelectMany(clause => clause.FlowIds)]
        };
    }

    /// <summary>
    /// The sentence naming the conditional flows <paramref name="element"/> never takes, with their ids, or <c>null</c>
    /// when it takes every one it has.
    /// </summary>
    private (string Sentence, IReadOnlyList<string> FlowIds)? IgnoredConditionalFlows(BpmnElement element, IReadOnlyCollection<BpmnSequenceFlow> taken)
    {
        var ignored = OutboundFlows(element.ElementId)
            .Where(flow => flow.ConditionOutcome is not null && !taken.Any(candidate => StringComparer.Ordinal.Equals(candidate.FlowId, flow.FlowId)))
            .Select(flow => flow.FlowId)
            .ToArray();
        if (ignored.Length == 0)
            return null;

        var binds = BpmnElementFamilies.Resolve(element) is BpmnElementFamilies.ExclusiveGateway or BpmnElementFamilies.InclusiveGateway
            ? "binds no decision"
            : "binds no work";
        var sentence = ignored.Length == 1
            ? $"'{element.ElementId}' {binds}, so its conditional flow '{ignored[0]}' is never taken."
            : $"'{element.ElementId}' {binds}, so its conditional flows {string.Join(", ", ignored.Select(id => $"'{id}'"))} are never taken.";
        return (sentence, ignored);
    }

    /// <summary>
    /// The sequence flows a token arriving at <paramref name="element"/> is certain to leave on in the same
    /// evaluation, or <c>null</c> when the element can hold the token. Read from the built-in behaviors and the
    /// interpreter's dispatch: a registry that replaces a built-in behavior changes what the interpreter does, but
    /// not this answer. A host whose registry routes differently has no way to override the check yet; the
    /// propagation limit still covers what the check lets through.
    /// <list type="bullet">
    /// <item>An element that binds work starts it, and the token waits for the host: every task including call
    /// activities, subprocesses, catch events, message throw events, and an exclusive or inclusive gateway bound
    /// to a decision. A multi-instance activity counts too, although an empty collection routes it on at once:
    /// that depends on data.</item>
    /// <item>A join, a parallel or inclusive gateway with more than one inbound flow, parks the token until it is
    /// ready. Whether a cycle through a join satisfies the join by itself is not worked out here; every join
    /// counts as able to wait.</item>
    /// <item>A compensate throw event parks the token while handlers run, whenever anything is registered for it
    /// to compensate.</item>
    /// <item>An escalation throw event caught by an interrupting event subprocess of this scope stops every other
    /// token in the scope, its own successor included.</item>
    /// </list>
    /// Everything else passes the token on. An unbound task, an unbound inclusive split, and any other escalation
    /// throw take their unconditional flows, else the default flow; an arriving token reports no outcome, so a
    /// flow conditioned on an outcome is never taken. An unbound exclusive gateway takes its only flow, else the
    /// first flow in declaration order without a condition, else its default. A parallel split and an event-based
    /// gateway take every flow, and the event-based gateway's targets are catch events, which wait. An element
    /// left with no flow to take ends the token or faults, so it is on no cycle.
    /// <para>
    /// A flow whose only condition is an expression is possibly taken: nothing here can ask the host's evaluator.
    /// It is never counted as certain, and while it could be taken neither is a flow the answer decides between,
    /// such as the default, or for an exclusive gateway any flow after it. So an element whose way off a cycle
    /// is an expression never closes a refused cycle through the alternative. An unconditional flow is still
    /// certain beside an expression at a task or inclusive split, because the split takes it whatever the answer.
    /// </para>
    /// </summary>
    private IReadOnlyCollection<BpmnSequenceFlow>? FlowsTakenWithoutWaiting(BpmnElement element)
    {
        if (element.BindingRef is not null || BpmnTokenCoordinator.ShouldWaitAtJoin(this, element.ElementId))
            return null;

        var outbound = OutboundFlows(element.ElementId);
        switch (BpmnElementFamilies.Resolve(element))
        {
            case BpmnElementFamilies.ParallelGateway:
            case BpmnElementFamilies.EventBasedGateway:
            case BpmnElementFamilies.IntermediateThrowEventLink:
                return outbound;
            case BpmnElementFamilies.ExclusiveGateway:
                return BpmnFlowSelector.CertainExclusiveFlowOnArrival(element, outbound);
            case BpmnElementFamilies.Task:
            case BpmnElementFamilies.InclusiveGateway:
            case BpmnElementFamilies.IntermediateCatchEventLink:
                return BpmnFlowSelector.CertainTaskFlowsOnArrival(element, outbound);
            case BpmnElementFamilies.IntermediateThrowEventEscalation:
                // Validation already refused a throw without a code; without one, nothing in the scope could catch it.
                return ReadEscalationCode(element) is { } code && EscalationEventSubprocess(code) is { Interrupting: true }
                    ? null
                    : BpmnFlowSelector.CertainTaskFlowsOnArrival(element, outbound);
            default:
                // Start, end and boundary events, which are on no cycle, and the compensate throw event.
                return null;
        }
    }

    private static IReadOnlyList<BpmnEventSubprocessCatcher> Validate(
        IReadOnlyCollection<BpmnElement> elements,
        IReadOnlyCollection<BpmnSequenceFlow> flows,
        IReadOnlyDictionary<string, BpmnBoundWork> boundWorkByRef,
        IReadOnlySet<string> declaredVariableNames,
        bool structureIsTransaction)
    {
        if (elements.Select(element => element.ElementId).Distinct(StringComparer.Ordinal).Count() != elements.Count)
            throw new BpmnExecutionException("BPMN structure contains duplicate element ids.");

        if (flows.Select(flow => flow.FlowId).Distinct(StringComparer.Ordinal).Count() != flows.Count)
            throw new BpmnExecutionException("BPMN structure contains duplicate sequence flow ids.");

        var elementIds = elements.Select(element => element.ElementId).ToHashSet(StringComparer.Ordinal);
        foreach (var flow in flows)
        {
            if (!elementIds.Contains(flow.SourceRef))
                throw new BpmnExecutionException($"BPMN sequence flow '{flow.FlowId}' source '{flow.SourceRef}' does not exist.") { ElementIds = [flow.FlowId, flow.SourceRef] };
            if (!elementIds.Contains(flow.TargetRef))
                throw new BpmnExecutionException($"BPMN sequence flow '{flow.FlowId}' target '{flow.TargetRef}' does not exist.") { ElementIds = [flow.FlowId, flow.TargetRef] };
        }

        var boundBindingRefs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in elements)
        {
            // Resolving the family also rejects unsupported element types and event definitions.
            var family = BpmnElementFamilies.Resolve(element);

            if (family == BpmnElementFamilies.StartEventTimer)
                _ = BpmnTimerStartSchedule.Resolve(element);

            // A start's cycle is checked by its schedule above, and an event-subprocess start's by its parent's
            // catcher below; a timer catch or boundary checks its own.
            if (family is BpmnElementFamilies.IntermediateCatchEvent or BpmnElementFamilies.BoundaryEvent
                && TimerDefinition(element) is { } timer)
                _ = BpmnTimerCycleRules.Resolve(timer.Properties, $"BPMN timer {element.ElementType} '{element.ElementId}'", [element.ElementId]);

            if (element.BindingRef is not null)
            {
                if (!boundWorkByRef.ContainsKey(element.BindingRef))
                    throw new BpmnExecutionException($"BPMN element '{element.ElementId}' binds work '{element.BindingRef}', which the host did not supply.") { ElementIds = [element.ElementId] };
                if (!boundBindingRefs.Add(element.BindingRef))
                    throw new BpmnExecutionException($"BPMN bound work '{element.BindingRef}' is bound by more than one element.");
            }

            // A scope-listener binding is a second work channel — a binding ref is used EITHER as some element's
            // BindingRef OR as some element's ListenerBindingRef, never both, never twice.
            if (element.ListenerBindingRef is not null)
            {
                if (!boundWorkByRef.ContainsKey(element.ListenerBindingRef))
                    throw new BpmnExecutionException($"BPMN element '{element.ElementId}' binds scope-listener work '{element.ListenerBindingRef}', which the host did not supply.") { ElementIds = [element.ElementId] };
                if (!boundBindingRefs.Add(element.ListenerBindingRef))
                    throw new BpmnExecutionException($"BPMN bound work '{element.ListenerBindingRef}' is bound by more than one element.");
            }

            switch (family)
            {
                case BpmnElementFamilies.StartEventNone:
                case BpmnElementFamilies.StartEventTimer:
                case BpmnElementFamilies.StartEventMessage:
                case BpmnElementFamilies.StartEventSignal:
                case BpmnElementFamilies.StartEventEscalation:
                case BpmnElementFamilies.StartEventError:
                case BpmnElementFamilies.EndEventNone:
                case BpmnElementFamilies.EndEventTerminate:
                case BpmnElementFamilies.EndEventError:
                case BpmnElementFamilies.ParallelGateway:
                case BpmnElementFamilies.EventBasedGateway:
                    if (element.BindingRef is not null)
                        throw new BpmnExecutionException($"BPMN element '{element.ElementId}' ({element.ElementType}) cannot bind work.") { ElementIds = [element.ElementId] };
                    break;
                case BpmnElementFamilies.SubProcess:
                    if (element.BindingRef is null)
                        throw new BpmnExecutionException($"BPMN subprocess '{element.ElementId}' requires bound work (a nested BPMN process).") { ElementIds = [element.ElementId] };
                    break;
                case BpmnElementFamilies.IntermediateCatchEvent:
                    if (element.BindingRef is null)
                        throw new BpmnExecutionException($"BPMN intermediate catch event '{element.ElementId}' requires bound suspending work (a timer, or a message or signal subscription).") { ElementIds = [element.ElementId] };
                    break;
                case BpmnElementFamilies.IntermediateThrowEventMessage:
                case BpmnElementFamilies.EndEventMessage:
                    // A message throw/end is a bound-work send — it binds the work that publishes the message.
                    if (element.BindingRef is null)
                        throw new BpmnExecutionException($"BPMN message {(StringComparer.Ordinal.Equals(element.ElementType, BpmnElementTypes.EndEvent) ? "end" : "throw")} event '{element.ElementId}' requires bound work (the send that publishes the message).") { ElementIds = [element.ElementId] };
                    break;
                case BpmnElementFamilies.IntermediateThrowEventSignal:
                case BpmnElementFamilies.EndEventSignal:
                    // A signal throw/end is a bound-work broadcast — it binds the work that publishes the signal.
                    if (element.BindingRef is null)
                        throw new BpmnExecutionException($"BPMN signal {(StringComparer.Ordinal.Equals(element.ElementType, BpmnElementTypes.EndEvent) ? "end" : "throw")} event '{element.ElementId}' requires bound work (the broadcast that publishes the signal).") { ElementIds = [element.ElementId] };
                    break;
            }
        }

        foreach (var unboundBindingRef in boundWorkByRef.Keys.Where(reference => !boundBindingRefs.Contains(reference)))
            throw new BpmnExecutionException($"BPMN bound work '{unboundBindingRef}' is not bound to any element.");

        var startEvents = elements.Where(element => StringComparer.Ordinal.Equals(element.ElementType, BpmnElementTypes.StartEvent)).ToArray();
        if (elements.Count > 0 && startEvents.Length == 0)
            throw new BpmnExecutionException("BPMN structure requires at least one start event.");

        var inboundByTarget = flows.ToLookup(flow => flow.TargetRef, StringComparer.Ordinal);
        var outboundBySource = flows.ToLookup(flow => flow.SourceRef, StringComparer.Ordinal);

        foreach (var startEvent in startEvents)
        {
            if (inboundByTarget[startEvent.ElementId].Any())
                throw new BpmnExecutionException($"BPMN start event '{startEvent.ElementId}' cannot have inbound sequence flows.") { ElementIds = [startEvent.ElementId] };
        }

        foreach (var endEvent in elements.Where(element => StringComparer.Ordinal.Equals(element.ElementType, BpmnElementTypes.EndEvent)))
        {
            if (outboundBySource[endEvent.ElementId].Any())
                throw new BpmnExecutionException($"BPMN end event '{endEvent.ElementId}' cannot have outbound sequence flows.") { ElementIds = [endEvent.ElementId] };
        }

        foreach (var element in elements)
        {
            var defaults = outboundBySource[element.ElementId].Where(flow => flow.IsDefault).ToArray();
            if (defaults.Length > 1)
                throw new BpmnExecutionException($"BPMN element '{element.ElementId}' declares more than one default sequence flow.") { ElementIds = [element.ElementId] };

            if (element.DefaultFlowId is not null)
            {
                var referenced = flows.FirstOrDefault(flow => StringComparer.Ordinal.Equals(flow.FlowId, element.DefaultFlowId))
                                 ?? throw new BpmnExecutionException($"BPMN element '{element.ElementId}' default flow '{element.DefaultFlowId}' does not exist.") { ElementIds = [element.ElementId, element.DefaultFlowId] };
                if (!StringComparer.Ordinal.Equals(referenced.SourceRef, element.ElementId))
                    throw new BpmnExecutionException($"BPMN element '{element.ElementId}' default flow '{element.DefaultFlowId}' does not originate from it.") { ElementIds = [element.ElementId, element.DefaultFlowId] };
                if (defaults.Length == 1 && !StringComparer.Ordinal.Equals(defaults[0].FlowId, element.DefaultFlowId))
                    throw new BpmnExecutionException($"BPMN element '{element.ElementId}' declares conflicting default flows '{defaults[0].FlowId}' and '{element.DefaultFlowId}'.") { ElementIds = [element.ElementId, defaults[0].FlowId, element.DefaultFlowId] };
            }
        }

        ValidateEventBasedGateways(elements, outboundBySource, inboundByTarget);

        RefuseExpressionsNothingEvaluates(elements, outboundBySource);

        ValidateBoundaryEvents(elements, outboundBySource, inboundByTarget);

        ValidateEscalation(elements);

        ValidateMessageThrowEnd(elements);

        ValidateCompensation(elements, outboundBySource, inboundByTarget);

        ValidateTransaction(elements, structureIsTransaction);

        ValidateMultiInstance(elements, declaredVariableNames);

        var eventSubprocesses = ValidateEventSubprocesses(elements, flows, boundWorkByRef);

        // Cyclic sequence flows are executable: loop-back edges become loop-iteration keys. The
        // structural rules above still constrain where a loop-back may land — a loop-back into a start event
        // (no inbound), a boundary event (no inbound), or an event-gateway-armed catch (exactly-one-inbound)
        // is rejected by those rules — so no acyclicity check remains. What a cycle must still be able to do is
        // wait, and that needs the built graph: see RefuseCyclesThatCannotWait.

        return eventSubprocesses;
    }

    /// <summary>
    /// Event subprocess rules. A <c>TriggeredByEvent</c> element is a flow-less <c>subProcess</c>
    /// binding a body: it participates in no sequence flows, carries no loop characteristics, hosts no attached
    /// boundary, is not a compensation handler, not referenced as one, and is neither a compensation handler nor a
    /// transaction (the compensation-handler rule family, mirrored). Its body must declare exactly one start event
    /// carrying exactly one supported trigger definition — escalation or error (each with an optional code;
    /// code-less = catch-all), or a message/signal (with a <c>name</c>) / timer (with an <c>interval</c>) — with no
    /// nested <c>TriggeredByEvent</c> inside the body (rejected). A message/signal/timer event subprocess
    /// declares a required <see cref="BpmnElement.ListenerBindingRef"/> (the armed scope listener) that
    /// escalation/error must NOT declare (dormant catchers). The body start event's interrupting flag (its
    /// <see cref="BpmnElement.CancelActivity"/>, the BPMN <c>isInterrupting</c> default-true convention) must be
    /// interrupting for an error trigger. Per scope and per trigger kind, escalation and error alike: codes are
    /// distinct, with at most one code-less catch-all. The body structure is read the way MI validation reads
    /// the definition's own variable declarations — authoring-time knowledge, no runtime cross-scope reach.
    /// Returns the graph-derived catcher index.
    /// </summary>
    private static IReadOnlyList<BpmnEventSubprocessCatcher> ValidateEventSubprocesses(
        IReadOnlyCollection<BpmnElement> elements,
        IReadOnlyCollection<BpmnSequenceFlow> flows,
        IReadOnlyDictionary<string, BpmnBoundWork> boundWorkByRef)
    {
        var triggered = elements.Where(element => element.TriggeredByEvent).ToArray();
        if (triggered.Length == 0)
            return [];

        var inboundByTarget = flows.ToLookup(flow => flow.TargetRef, StringComparer.Ordinal);
        var outboundBySource = flows.ToLookup(flow => flow.SourceRef, StringComparer.Ordinal);
        var boundaryHosts = elements
            .Where(element => StringComparer.Ordinal.Equals(element.ElementType, BpmnElementTypes.BoundaryEvent) && element.AttachedToRef is not null)
            .Select(element => element.AttachedToRef!)
            .ToHashSet(StringComparer.Ordinal);
        var compensationHandlerRefs = elements
            .Where(element => element.CompensationHandlerElementId is not null)
            .Select(element => element.CompensationHandlerElementId!)
            .ToHashSet(StringComparer.Ordinal);

        var catchers = new List<BpmnEventSubprocessCatcher>();
        var escalationCodes = new HashSet<string>(StringComparer.Ordinal);
        var hasEscalationCatchAll = false;
        var errorCodes = new HashSet<string>(StringComparer.Ordinal);
        var hasErrorCatchAll = false;

        foreach (var element in triggered)
        {
            // Rule 1 — element-level shape (the compensation-handler rule family, mirrored).
            if (!StringComparer.Ordinal.Equals(element.ElementType, BpmnElementTypes.SubProcess))
                throw new BpmnExecutionException($"BPMN element '{element.ElementId}' ({element.ElementType}) is marked as an event subprocess, but only a subprocess element may be an event subprocess.") { ElementIds = [element.ElementId] };
            if (element.BindingRef is null)
                throw new BpmnExecutionException($"BPMN event subprocess '{element.ElementId}' requires bound work (the nested event-subprocess body).") { ElementIds = [element.ElementId] };
            if (inboundByTarget[element.ElementId].Any() || outboundBySource[element.ElementId].Any())
                throw new BpmnExecutionException($"BPMN event subprocess '{element.ElementId}' cannot participate in sequence flows; it is activated by its body start-event trigger, not by token flow.") { ElementIds = [element.ElementId] };
            if (element.LoopCharacteristics is not null)
                throw new BpmnExecutionException($"BPMN event subprocess '{element.ElementId}' cannot carry multi-instance loop characteristics.") { ElementIds = [element.ElementId] };
            if (boundaryHosts.Contains(element.ElementId))
                throw new BpmnExecutionException($"BPMN event subprocess '{element.ElementId}' cannot host attached boundary events.") { ElementIds = [element.ElementId] };
            if (element.IsForCompensation)
                throw new BpmnExecutionException($"BPMN event subprocess '{element.ElementId}' cannot also be a compensation handler.") { ElementIds = [element.ElementId] };
            if (compensationHandlerRefs.Contains(element.ElementId))
                throw new BpmnExecutionException($"BPMN event subprocess '{element.ElementId}' cannot be referenced as a compensation handler.") { ElementIds = [element.ElementId] };
            if (element.IsTransaction)
                throw new BpmnExecutionException($"BPMN event subprocess '{element.ElementId}' cannot also be a transaction.") { ElementIds = [element.ElementId] };

            // Rule 2 — body structure: exactly one start event with one supported trigger definition; no nested
            // event subprocess. The body definition travels on the bound work.
            var bodyStructure = boundWorkByRef.TryGetValue(element.BindingRef, out var bodyWork) ? bodyWork.NestedProcess : null;
            if (bodyStructure is null)
                throw new BpmnExecutionException($"BPMN event subprocess '{element.ElementId}' body must be a nested BPMN process (its bound work carries none).") { ElementIds = [element.ElementId] };
            if (bodyStructure.Elements.Any(bodyElement => bodyElement.TriggeredByEvent))
                throw new BpmnExecutionException($"BPMN event subprocess '{element.ElementId}' body declares a nested event subprocess; nested event subprocesses are not supported.") { ElementIds = [element.ElementId] };

            var bodyStarts = bodyStructure.Elements
                .Where(bodyElement => StringComparer.Ordinal.Equals(bodyElement.ElementType, BpmnElementTypes.StartEvent))
                .ToArray();
            if (bodyStarts.Length != 1)
                throw new BpmnExecutionException($"BPMN event subprocess '{element.ElementId}' body must declare exactly one start event; it declares {bodyStarts.Length}.") { ElementIds = [element.ElementId] };
            var bodyStart = bodyStarts[0];
            if (bodyStart.EventDefinitions.Count != 1)
                throw new BpmnExecutionException($"BPMN event subprocess '{element.ElementId}' body start event '{bodyStart.ElementId}' must declare exactly one event definition of a supported trigger (escalation, error, message, signal, or timer); it declares {bodyStart.EventDefinitions.Count}.") { ElementIds = [element.ElementId, bodyStart.ElementId] };

            // Rule 3 — interrupting flag from the body start event's CancelActivity (isInterrupting, default true).
            var interrupting = bodyStart.CancelActivity;
            var definitionType = bodyStart.EventDefinitions.Single().Type;

            if (StringComparer.Ordinal.Equals(definitionType, BpmnEventDefinitionTypes.Escalation))
            {
                // Escalation and error are dormant catchers — they must NOT declare a scope listener.
                if (element.ListenerBindingRef is not null)
                    throw new BpmnExecutionException($"BPMN escalation event subprocess '{element.ElementId}' declares scope-listener work, but escalation event subprocesses are dormant (activated by a signal, not an armed listener); only message/signal/timer event subprocesses may declare a listener.") { ElementIds = [element.ElementId] };

                var code = ReadEscalationCode(bodyStart);
                if (code is null)
                {
                    if (hasEscalationCatchAll)
                        throw new BpmnExecutionException($"BPMN scope declares more than one code-less catch-all escalation event subprocess (at '{element.ElementId}'); a scope may carry at most one.") { ElementIds = [element.ElementId] };
                    hasEscalationCatchAll = true;
                }
                else if (!escalationCodes.Add(code))
                {
                    throw new BpmnExecutionException($"BPMN scope declares more than one escalation event subprocess for code '{code}' (at '{element.ElementId}'); escalation event-subprocess codes in one scope must be distinct.") { ElementIds = [element.ElementId] };
                }

                catchers.Add(new BpmnEventSubprocessCatcher(element.ElementId, element.BindingRef, bodyStart.ElementId, BpmnEventSubprocessTriggerKind.Escalation, code, interrupting));
            }
            else if (StringComparer.Ordinal.Equals(definitionType, BpmnEventDefinitionTypes.Error))
            {
                // An error-triggered event subprocess catches an error raised in this scope — a work fault, a call
                // activity's failure outcome, or an error end event — and then runs its body. Interrupting only (per
                // BPMN). A coded one catches only its code, so a scope may carry several with distinct codes plus one
                // code-less catch-all: the escalation rule, applied to errors.
                if (element.ListenerBindingRef is not null)
                    throw new BpmnExecutionException($"BPMN error event subprocess '{element.ElementId}' declares scope-listener work, but error event subprocesses are dormant (activated by an error, not an armed listener); only message/signal/timer event subprocesses may declare a listener.") { ElementIds = [element.ElementId] };
                if (!interrupting)
                    throw new BpmnExecutionException($"BPMN error event subprocess '{element.ElementId}' must be interrupting; error events are always interrupting per BPMN.") { ElementIds = [element.ElementId] };

                var errorCode = ReadErrorCode(bodyStart);
                if (errorCode is null)
                {
                    if (hasErrorCatchAll)
                        throw new BpmnExecutionException($"BPMN scope declares more than one code-less catch-all error event subprocess (at '{element.ElementId}'); a scope may carry at most one.") { ElementIds = [element.ElementId] };
                    hasErrorCatchAll = true;
                }
                else if (!errorCodes.Add(errorCode))
                {
                    throw new BpmnExecutionException($"BPMN scope declares more than one error event subprocess for code '{errorCode}' (at '{element.ElementId}'); error event-subprocess codes in one scope must be distinct.") { ElementIds = [element.ElementId] };
                }

                catchers.Add(new BpmnEventSubprocessCatcher(element.ElementId, element.BindingRef, bodyStart.ElementId, BpmnEventSubprocessTriggerKind.Error, errorCode, Interrupting: true));
            }
            else if (ExternalTriggerKind(definitionType) is { } externalTriggerKind)
            {
                // A message/signal/timer-triggered event subprocess needs an armed scope listener — a required
                // ListenerBindingRef naming the suspending work the host arms. Its binding was accounted for in the
                // exactly-one-binding loop. The body start's trigger facts (message/signal name; timer interval, date or
                // cycle) are validated non-empty; a timer cycle's repetition count bounds its non-interrupting re-arms.
                if (element.ListenerBindingRef is null)
                    throw new BpmnExecutionException($"BPMN {definitionType} event subprocess '{element.ElementId}' requires scope-listener work (a message/signal/timer event subprocess arms a suspending listener at scope start); none is bound.") { ElementIds = [element.ElementId] };

                var cycle = ValidateExternalTriggerDefinition(element, bodyStart, definitionType);
                catchers.Add(new BpmnEventSubprocessCatcher(element.ElementId, element.BindingRef, bodyStart.ElementId, externalTriggerKind, Code: null, interrupting, element.ListenerBindingRef)
                {
                    FiresOnce = externalTriggerKind == BpmnEventSubprocessTriggerKind.Timer
                                && HasNonBlank(bodyStart.EventDefinitions.Single().Properties, BpmnEventDefinitionProperties.Date),
                    Repetitions = cycle?.Repetitions
                });
            }
            else
            {
                throw new BpmnExecutionException($"BPMN event subprocess '{element.ElementId}' body start event '{bodyStart.ElementId}' declares an unsupported trigger definition '{definitionType}'; only escalation, error, message, signal, and timer triggers are supported.") { ElementIds = [element.ElementId, bodyStart.ElementId] };
            }
        }

        return catchers;
    }

    /// <summary>The external-trigger event-subprocess kind of a body-start definition type, or <c>null</c> for a non-external type.</summary>
    private static BpmnEventSubprocessTriggerKind? ExternalTriggerKind(string definitionType) =>
        StringComparer.Ordinal.Equals(definitionType, BpmnEventDefinitionTypes.Message) ? BpmnEventSubprocessTriggerKind.Message
        : StringComparer.Ordinal.Equals(definitionType, BpmnEventDefinitionTypes.Signal) ? BpmnEventSubprocessTriggerKind.Signal
        : StringComparer.Ordinal.Equals(definitionType, BpmnEventDefinitionTypes.Timer) ? BpmnEventSubprocessTriggerKind.Timer
        : null;

    /// <summary>
    /// Validates a message/signal/timer event-subprocess body start's stimulus facts: message/signal
    /// declare a non-empty <c>name</c>; a timer declares a <c>cycle</c> (<c>timeCycle</c>-shaped, see
    /// <see cref="BpmnTimerCycleRules"/>), or else exactly one non-empty <c>interval</c> (<c>timeDuration</c>-shaped) or
    /// <c>date</c> (<c>timeDate</c>-shaped), and no <c>cron</c> outside a cycle. Returns the timer's parsed cycle, or
    /// <c>null</c> when the trigger is not a timer on a cycle.
    /// </summary>
    private static BpmnTimerCycle? ValidateExternalTriggerDefinition(BpmnElement element, BpmnElement bodyStart, string definitionType)
    {
        var properties = bodyStart.EventDefinitions.Single().Properties;
        if (StringComparer.Ordinal.Equals(definitionType, BpmnEventDefinitionTypes.Timer))
        {
            if (BpmnTimerCycleRules.Resolve(properties, $"BPMN timer event subprocess '{element.ElementId}' body start event '{bodyStart.ElementId}'", [element.ElementId, bodyStart.ElementId]) is { } cycle)
            {
                return cycle;
            }
            if (properties.TryGetValue(BpmnEventDefinitionProperties.Cron, out var cron) && !string.IsNullOrWhiteSpace(cron))
                throw new BpmnExecutionException($"BPMN timer event subprocess '{element.ElementId}' declares a cron schedule without a cycle; a cron timer event subprocess declares its timeCycle as '{BpmnEventDefinitionProperties.Cycle}'.") { ElementIds = [element.ElementId] };
            if (!HasNonBlank(properties, BpmnEventDefinitionProperties.Interval) && !HasNonBlank(properties, BpmnEventDefinitionProperties.Date))
                throw new BpmnExecutionException($"BPMN timer event subprocess '{element.ElementId}' body start event '{bodyStart.ElementId}' declares no interval or date; a timer event subprocess needs a one-shot duration interval or a date.") { ElementIds = [element.ElementId, bodyStart.ElementId] };
            // Both would leave whether the listener re-arms after a non-interrupting fire to whichever one wins.
            if (HasNonBlank(properties, BpmnEventDefinitionProperties.Interval) && HasNonBlank(properties, BpmnEventDefinitionProperties.Date))
                throw new BpmnExecutionException($"BPMN timer event subprocess '{element.ElementId}' body start event '{bodyStart.ElementId}' declares both an interval and a date; a timer event subprocess needs exactly one.") { ElementIds = [element.ElementId, bodyStart.ElementId] };
            return null;
        }

        if (!properties.TryGetValue(BpmnEventDefinitionProperties.Name, out var name) || string.IsNullOrWhiteSpace(name))
            throw new BpmnExecutionException($"BPMN {definitionType} event subprocess '{element.ElementId}' body start event '{bodyStart.ElementId}' declares no event name; a {definitionType} event subprocess resolves its stimulus from a non-empty name.") { ElementIds = [element.ElementId, bodyStart.ElementId] };
        return null;
    }

    /// <summary>An element's timer event definition, or <c>null</c> when it declares none.</summary>
    internal static BpmnEventDefinition? TimerDefinition(BpmnElement element) =>
        element.EventDefinitions.FirstOrDefault(definition => StringComparer.Ordinal.Equals(definition.Type, BpmnEventDefinitionTypes.Timer));

    private static bool HasNonBlank(IReadOnlyDictionary<string, string> properties, string key) =>
        properties.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value);

    /// <summary>
    /// Multi-instance loop rules: loop characteristics are valid only on a task-family or
    /// <c>subProcess</c> host that binds work; exactly one of cardinality (≥ 1) XOR collection variable is
    /// set; a collection variable must name a declared container-scoped variable, and the item variable must not
    /// shadow the reserved <c>loopIndex</c> key.
    /// </summary>
    private static void ValidateMultiInstance(IReadOnlyCollection<BpmnElement> elements, IReadOnlySet<string> declaredVariableNames)
    {
        foreach (var element in elements)
        {
            if (element.LoopCharacteristics is not { } loop)
                continue;

            if (!BpmnElementFamilies.IsBoundaryHostFamily(element) || element.BindingRef is null)
                throw new BpmnExecutionException($"BPMN element '{element.ElementId}' ({element.ElementType}) declares multi-instance loop characteristics but is not a task-family or subprocess host with bound work.") { ElementIds = [element.ElementId] };

            var hasCardinality = loop.Cardinality is not null;
            var hasCollection = loop.CollectionVariable is not null;
            if (hasCardinality == hasCollection)
                throw new BpmnExecutionException($"BPMN multi-instance element '{element.ElementId}' must declare exactly one of a positive cardinality or a collection variable.") { ElementIds = [element.ElementId] };

            if (hasCardinality && loop.Cardinality < 1)
                throw new BpmnExecutionException($"BPMN multi-instance element '{element.ElementId}' declares a cardinality of {loop.Cardinality}; the cardinality must be a positive integer.") { ElementIds = [element.ElementId] };

            if (hasCollection && !declaredVariableNames.Contains(loop.CollectionVariable!))
                throw new BpmnExecutionException($"BPMN multi-instance element '{element.ElementId}' names collection variable '{loop.CollectionVariable}', which is not a declared container-scoped variable of the process.") { ElementIds = [element.ElementId] };

            // The item key must not collide with the reserved loopIndex key the iteration frame always seeds
            // — a collision would silently overwrite one with the other.
            if (hasCollection && StringComparer.Ordinal.Equals(loop.ItemVariable, BpmnLoopCharacteristics.LoopIndexVariable))
                throw new BpmnExecutionException($"BPMN multi-instance element '{element.ElementId}' uses item variable '{BpmnLoopCharacteristics.LoopIndexVariable}', which is reserved for the zero-based iteration index; choose a different item variable.") { ElementIds = [element.ElementId] };
        }
    }

    /// <summary>
    /// Boundary event rules: the <c>attachedToRef</c> host must exist, be a task-family or
    /// subprocess element, and bind work; a boundary takes no inbound flows and at least one
    /// outbound flow; a catch boundary (timer/message/signal) binds listener work while an error
    /// boundary binds none and must be interrupting; a host's error boundary codes are distinct, with at most one
    /// code-less catch-all; a boundary declares no default flow (its outbound is taken unconditionally when it fires).
    /// </summary>
    private static void ValidateBoundaryEvents(
        IReadOnlyCollection<BpmnElement> elements,
        ILookup<string, BpmnSequenceFlow> outboundBySource,
        ILookup<string, BpmnSequenceFlow> inboundByTarget)
    {
        var elementsById = elements.ToDictionary(element => element.ElementId, StringComparer.Ordinal);
        var errorCodesByHost = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var errorCatchAllHosts = new HashSet<string>(StringComparer.Ordinal);

        foreach (var boundary in elements.Where(element => StringComparer.Ordinal.Equals(element.ElementType, BpmnElementTypes.BoundaryEvent)))
        {
            if (boundary.AttachedToRef is not { } hostId)
                throw new BpmnExecutionException($"BPMN boundary event '{boundary.ElementId}' must declare an attachedToRef host element.") { ElementIds = [boundary.ElementId] };

            if (!elementsById.TryGetValue(hostId, out var host))
                throw new BpmnExecutionException($"BPMN boundary event '{boundary.ElementId}' is attached to '{hostId}', which does not exist.") { ElementIds = [boundary.ElementId, hostId] };

            if (!BpmnElementFamilies.IsBoundaryHostFamily(host))
                throw new BpmnExecutionException($"BPMN boundary event '{boundary.ElementId}' is attached to '{hostId}' ({host.ElementType}), which is not a task-family or subprocess host.") { ElementIds = [boundary.ElementId, hostId] };

            if (host.BindingRef is null)
                throw new BpmnExecutionException($"BPMN boundary event '{boundary.ElementId}' is attached to host '{hostId}', which binds no work; a boundary event can only attach to a host that runs work.") { ElementIds = [boundary.ElementId, hostId] };

            if (inboundByTarget[boundary.ElementId].Any())
                throw new BpmnExecutionException($"BPMN boundary event '{boundary.ElementId}' cannot have inbound sequence flows; it is entered by its host's activation.") { ElementIds = [boundary.ElementId] };

            // A compensation boundary inverts the outbound rule — it has zero outbound flows (its
            // "outbound" is the association to its handler); every other boundary needs at least one.
            var isCompensationBoundary = BpmnElementFamilies.IsCompensationBoundary(boundary);
            if (isCompensationBoundary)
            {
                if (outboundBySource[boundary.ElementId].Any())
                    throw new BpmnExecutionException($"BPMN compensation boundary event '{boundary.ElementId}' cannot have outbound sequence flows; its handler is reached by association, not by token flow.") { ElementIds = [boundary.ElementId] };
            }
            else if (!outboundBySource[boundary.ElementId].Any())
            {
                throw new BpmnExecutionException($"BPMN boundary event '{boundary.ElementId}' must have at least one outbound sequence flow.") { ElementIds = [boundary.ElementId] };
            }

            if (boundary.DefaultFlowId is not null || outboundBySource[boundary.ElementId].Any(flow => flow.IsDefault))
                throw new BpmnExecutionException($"BPMN boundary event '{boundary.ElementId}' cannot declare a default sequence flow; its outbound is taken unconditionally when it fires.") { ElementIds = [boundary.ElementId] };

            if (isCompensationBoundary)
            {
                // A compensation boundary binds no listener (CancelActivity is ignored);
                // its host must not carry loop characteristics (multi-instance host compensation is a stated cut).
                // The handler reference is validated in ValidateCompensation.
                if (boundary.BindingRef is not null)
                    throw new BpmnExecutionException($"BPMN compensation boundary event '{boundary.ElementId}' cannot bind work; it has no listener and fires after its host completes.") { ElementIds = [boundary.ElementId] };
                if (host.LoopCharacteristics is not null)
                    throw new BpmnExecutionException($"BPMN compensation boundary event '{boundary.ElementId}' is attached to multi-instance host '{hostId}'; compensation on a multi-instance host is not supported.") { ElementIds = [boundary.ElementId, hostId] };
            }
            else if (BpmnElementFamilies.IsErrorBoundary(boundary))
            {
                if (boundary.BindingRef is not null)
                    throw new BpmnExecutionException($"BPMN error boundary event '{boundary.ElementId}' cannot bind work; it catches the host's work fault and has no listener.") { ElementIds = [boundary.ElementId] };
                if (!boundary.CancelActivity)
                    throw new BpmnExecutionException($"BPMN error boundary event '{boundary.ElementId}' must be interrupting (cancelActivity=true); a non-interrupting error boundary is not meaningful.") { ElementIds = [boundary.ElementId] };

                // A coded boundary catches only its code, so a host may carry several: distinct codes plus at most
                // one code-less catch-all, the escalation rule applied to errors.
                var code = ReadErrorCode(boundary);
                if (code is null)
                {
                    if (!errorCatchAllHosts.Add(hostId))
                        throw new BpmnExecutionException($"BPMN host '{hostId}' declares more than one code-less catch-all error boundary; a host may carry at most one.") { ElementIds = [hostId] };
                }
                else
                {
                    var codes = errorCodesByHost.TryGetValue(hostId, out var existing) ? existing : errorCodesByHost[hostId] = new HashSet<string>(StringComparer.Ordinal);
                    if (!codes.Add(code))
                        throw new BpmnExecutionException($"BPMN host '{hostId}' declares more than one error boundary for code '{code}'; error boundary codes on one host must be distinct.") { ElementIds = [hostId] };
                }
            }
            else if (BpmnElementFamilies.IsCancelBoundary(boundary))
            {
                // A cancel boundary is dormant (no listener, like error and compensation) and routes its
                // outbound flows (≥1, already enforced above) when the transaction completes Cancelled. CancelActivity
                // is ignored (the host is already finished when it fires). The host-is-a-transaction and
                // at-most-one-per-host rules live in ValidateTransaction.
                if (boundary.BindingRef is not null)
                    throw new BpmnExecutionException($"BPMN cancel boundary event '{boundary.ElementId}' cannot bind work; it is dormant and fires on the transaction's Cancelled outcome.") { ElementIds = [boundary.ElementId] };
            }
            else if (BpmnElementFamilies.IsEscalationBoundary(boundary))
            {
                // An escalation boundary is dormant (no listener) and routes its outbound flows
                // (≥1, already enforced above) when a matching escalation notification arrives. CancelActivity is
                // honored (interrupting and non-interrupting are both meaningful). The host-must-be-a-subprocess,
                // distinct-codes, and single-catch-all rules live in ValidateEscalation.
                if (boundary.BindingRef is not null)
                    throw new BpmnExecutionException($"BPMN escalation boundary event '{boundary.ElementId}' cannot bind work; it is dormant and fires when a matching escalation signal arrives.") { ElementIds = [boundary.ElementId] };
            }
            else if (boundary.BindingRef is null)
            {
                throw new BpmnExecutionException($"BPMN catch boundary event '{boundary.ElementId}' requires bound suspending listener work (a timer, or a message or signal subscription).") { ElementIds = [boundary.ElementId] };
            }
        }
    }

    /// <summary>
    /// Escalation rules. An escalation throw (intermediate) or end event must carry a non-empty
    /// escalation <c>code</c> (a throw must say what it escalates) and bind no work. An escalation boundary must
    /// bind no work (already enforced in <see cref="ValidateBoundaryEvents"/>), attach to a <c>subProcess</c>-family
    /// host (a task host is dead by construction — a task's bound work is a leaf that can never escalate), and per
    /// host the escalation boundary codes must be distinct with at most one code-less catch-all. Each rule rejects
    /// deterministically with the offending element named.
    /// </summary>
    private static void ValidateEscalation(IReadOnlyCollection<BpmnElement> elements)
    {
        var elementsById = elements.ToDictionary(element => element.ElementId, StringComparer.Ordinal);

        // Rule 1 — an escalation throw/end carries a non-empty code and binds no work.
        foreach (var thrower in elements.Where(BpmnElementFamilies.IsEscalationThrowOrEnd))
        {
            if (thrower.BindingRef is not null)
                throw new BpmnExecutionException($"BPMN escalation {(StringComparer.Ordinal.Equals(thrower.ElementType, BpmnElementTypes.EndEvent) ? "end" : "throw")} event '{thrower.ElementId}' cannot bind work.") { ElementIds = [thrower.ElementId] };
            if (ReadEscalationCode(thrower) is null)
                throw new BpmnExecutionException($"BPMN escalation {(StringComparer.Ordinal.Equals(thrower.ElementType, BpmnElementTypes.EndEvent) ? "end" : "throw")} event '{thrower.ElementId}' must declare a non-empty escalation code; a throw must say what it escalates.") { ElementIds = [thrower.ElementId] };
        }

        // Rules 2/3 — escalation boundary host family + per-host distinct codes and a single catch-all.
        var codesByHost = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var catchAllByHost = new HashSet<string>(StringComparer.Ordinal);
        foreach (var boundary in elements.Where(BpmnElementFamilies.IsEscalationBoundary))
        {
            // ValidateBoundaryEvents already rejects a missing/unresolvable host, so a resolvable host is expected here.
            if (boundary.AttachedToRef is not { } hostId || !elementsById.TryGetValue(hostId, out var host))
                continue;
            if (!StringComparer.Ordinal.Equals(host.ElementType, BpmnElementTypes.SubProcess))
                throw new BpmnExecutionException($"BPMN escalation boundary event '{boundary.ElementId}' is attached to '{hostId}' ({host.ElementType}); an escalation boundary may only attach to a subprocess host (a task's bound work is a leaf that can never escalate).") { ElementIds = [boundary.ElementId, hostId] };

            var code = ReadEscalationCode(boundary);
            if (code is null)
            {
                if (!catchAllByHost.Add(hostId))
                    throw new BpmnExecutionException($"BPMN host '{hostId}' declares more than one code-less catch-all escalation boundary; a host may carry at most one.") { ElementIds = [hostId] };
            }
            else
            {
                var codes = codesByHost.TryGetValue(hostId, out var existing) ? existing : codesByHost[hostId] = new HashSet<string>(StringComparer.Ordinal);
                if (!codes.Add(code))
                    throw new BpmnExecutionException($"BPMN host '{hostId}' declares more than one escalation boundary for code '{code}'; escalation boundary codes on one host must be distinct.") { ElementIds = [hostId] };
            }
        }
    }

    /// <summary>
    /// The code an error event definition declares — what a catcher catches, or what an error end event throws — or
    /// <c>null</c> for a catch-all, or for a thrown error with no code. Errors keep their code under the same key as
    /// escalations; surrounding whitespace is trimmed, and a blank code counts as none.
    /// </summary>
    internal static string? ReadErrorCode(BpmnElement element) => ReadEscalationCode(element);

    /// <summary>The non-empty escalation code an escalation throw/end/boundary declares, or <c>null</c> when absent/blank (a code-less boundary is the catch-all).</summary>
    private static string? ReadEscalationCode(BpmnElement element) =>
        element.EventDefinitions.SingleOrDefault() is { } definition
        && definition.Properties.TryGetValue(BpmnEventDefinitionProperties.Code, out var code)
        && !string.IsNullOrWhiteSpace(code)
            ? code.Trim()
            : null;

    /// <summary>
    /// Message and signal throw/end rules. A message throw/end event is a bound-work send and a signal throw/end
    /// event a bound-work broadcast: its single event definition must carry a non-empty <c>name</c> (a send or
    /// broadcast must say what it publishes), and it binds the work that publishes it (the bound-work requirement
    /// is enforced in the per-family switch above). Rejects deterministically with the offending element named.
    /// </summary>
    private static void ValidateMessageThrowEnd(IReadOnlyCollection<BpmnElement> elements)
    {
        foreach (var sender in elements.Where(BpmnElementFamilies.IsMessageThrowOrEnd))
        {
            if (ReadMessageName(sender) is null)
                throw new BpmnExecutionException($"BPMN message {(StringComparer.Ordinal.Equals(sender.ElementType, BpmnElementTypes.EndEvent) ? "end" : "throw")} event '{sender.ElementId}' must declare a non-empty message name; a send must say what it publishes.") { ElementIds = [sender.ElementId] };
        }

        foreach (var broadcaster in elements.Where(BpmnElementFamilies.IsSignalThrowOrEnd))
        {
            if (ReadMessageName(broadcaster) is null)
                throw new BpmnExecutionException($"BPMN signal {(StringComparer.Ordinal.Equals(broadcaster.ElementType, BpmnElementTypes.EndEvent) ? "end" : "throw")} event '{broadcaster.ElementId}' must declare a non-empty signal name; a broadcast must say what it publishes.") { ElementIds = [broadcaster.ElementId] };
        }
    }

    private static string? ReadMessageName(BpmnElement element) =>
        element.EventDefinitions.SingleOrDefault() is { } definition
        && definition.Properties.TryGetValue(BpmnEventDefinitionProperties.Name, out var name)
        && !string.IsNullOrWhiteSpace(name)
            ? name.Trim()
            : null;

    /// <summary>
    /// Compensation rules. Compensation boundaries reference an existing handler; handlers are
    /// task-family/subProcess elements that bind work, take zero sequence flows, carry no loop characteristics,
    /// host no boundary, and are referenced by exactly one compensation boundary (orphans rejected); compensate
    /// throw events bind no work; an <c>activityRef</c> on a compensate throw/end names an existing element with
    /// an attached compensation boundary; and handlers never sit at a start/boundary-host/throw slot. Each rule
    /// rejects deterministically with the offending element named.
    /// </summary>
    private static void ValidateCompensation(
        IReadOnlyCollection<BpmnElement> elements,
        ILookup<string, BpmnSequenceFlow> outboundBySource,
        ILookup<string, BpmnSequenceFlow> inboundByTarget)
    {
        var elementsById = elements.ToDictionary(element => element.ElementId, StringComparer.Ordinal);
        var boundariesByHandler = elements
            .Where(BpmnElementFamilies.IsCompensationBoundary)
            .Where(boundary => boundary.CompensationHandlerElementId is not null)
            .ToLookup(boundary => boundary.CompensationHandlerElementId!, StringComparer.Ordinal);
        var attachedByHost = elements
            .Where(element => StringComparer.Ordinal.Equals(element.ElementType, BpmnElementTypes.BoundaryEvent) && element.AttachedToRef is not null)
            .ToLookup(element => element.AttachedToRef!, StringComparer.Ordinal);

        // Rule 1 — a compensation boundary references an existing handler.
        foreach (var boundary in elements.Where(BpmnElementFamilies.IsCompensationBoundary))
        {
            if (boundary.CompensationHandlerElementId is not { } handlerId)
                throw new BpmnExecutionException($"BPMN compensation boundary event '{boundary.ElementId}' must reference a compensation handler via its compensation-handler element id.") { ElementIds = [boundary.ElementId] };
            if (!elementsById.TryGetValue(handlerId, out var referenced))
                throw new BpmnExecutionException($"BPMN compensation boundary event '{boundary.ElementId}' references handler '{handlerId}', which does not exist.") { ElementIds = [boundary.ElementId, handlerId] };
            if (!referenced.IsForCompensation)
                throw new BpmnExecutionException($"BPMN compensation boundary event '{boundary.ElementId}' references '{handlerId}', which is not a compensation handler element (isForCompensation).") { ElementIds = [boundary.ElementId, handlerId] };
        }

        // Rule 2 — handler shape (task-family/subProcess, binds work, zero flows, no loop characteristics, hosts
        // no boundary, referenced by exactly one compensation boundary; orphans rejected).
        foreach (var handler in elements.Where(element => element.IsForCompensation))
        {
            if (!BpmnElementFamilies.IsBoundaryHostFamily(handler))
                throw new BpmnExecutionException($"BPMN compensation handler '{handler.ElementId}' ({handler.ElementType}) must be a task-family or subprocess element.") { ElementIds = [handler.ElementId] };
            if (handler.BindingRef is null)
                throw new BpmnExecutionException($"BPMN compensation handler '{handler.ElementId}' must bind work.") { ElementIds = [handler.ElementId] };
            if (inboundByTarget[handler.ElementId].Any() || outboundBySource[handler.ElementId].Any())
                throw new BpmnExecutionException($"BPMN compensation handler '{handler.ElementId}' cannot participate in sequence flows; it is invoked only by the compensation replay.") { ElementIds = [handler.ElementId] };
            if (handler.LoopCharacteristics is not null)
                throw new BpmnExecutionException($"BPMN compensation handler '{handler.ElementId}' cannot carry loop characteristics.") { ElementIds = [handler.ElementId] };
            if (attachedByHost[handler.ElementId].Any())
                throw new BpmnExecutionException($"BPMN compensation handler '{handler.ElementId}' cannot host attached boundary events.") { ElementIds = [handler.ElementId] };

            var references = boundariesByHandler[handler.ElementId].Count();
            if (references == 0)
                throw new BpmnExecutionException($"BPMN compensation handler '{handler.ElementId}' is referenced by no compensation boundary (orphan handler); it cannot ride normal flow.") { ElementIds = [handler.ElementId] };
            if (references > 1)
                throw new BpmnExecutionException($"BPMN compensation handler '{handler.ElementId}' is referenced by more than one compensation boundary; a handler binds exactly one boundary.") { ElementIds = [handler.ElementId] };
        }

        // Rule 3 — a handler never sits at a start-event slot (its zero-flows rule already blocks it from
        // gateway targets and boundary-host slots, which are reached/entered by flows or activation).
        foreach (var start in elements.Where(element => StringComparer.Ordinal.Equals(element.ElementType, BpmnElementTypes.StartEvent) && element.IsForCompensation))
            throw new BpmnExecutionException($"BPMN start event '{start.ElementId}' cannot be a compensation handler.") { ElementIds = [start.ElementId] };

        // Rule 4 — a compensation-handler reference belongs only on a compensation boundary, and a compensate
        // throw event binds no work.
        foreach (var element in elements.Where(element => element.CompensationHandlerElementId is not null && !BpmnElementFamilies.IsCompensationBoundary(element)))
            throw new BpmnExecutionException($"BPMN element '{element.ElementId}' declares a compensation-handler reference but is not a compensation boundary event.") { ElementIds = [element.ElementId] };

        // A message throw is also an intermediateThrowEvent but IS a bound-work send, so this compensate-only
        // rule is scoped to compensate throws; escalation throws are handled by ValidateEscalation.
        foreach (var throwEvent in elements.Where(element => StringComparer.Ordinal.Equals(element.ElementType, BpmnElementTypes.IntermediateThrowEvent) && BpmnElementFamilies.HasCompensateDefinition(element)))
        {
            if (throwEvent.BindingRef is not null)
                throw new BpmnExecutionException($"BPMN compensate throw event '{throwEvent.ElementId}' cannot bind work.") { ElementIds = [throwEvent.ElementId] };
        }

        // Rule 5 — an activityRef on a compensate throw/end names an existing element with an attached
        // compensation boundary in this process.
        var compensationHosts = elements
            .Where(BpmnElementFamilies.IsCompensationBoundary)
            .Where(boundary => boundary.AttachedToRef is not null)
            .Select(boundary => boundary.AttachedToRef!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var thrower in elements.Where(IsCompensateThrowOrEnd))
        {
            var activityRef = thrower.EventDefinitions.Single().Properties.TryGetValue(BpmnEventDefinitionProperties.ActivityRef, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value.Trim()
                : null;
            if (activityRef is null)
                continue;
            if (!elementsById.ContainsKey(activityRef))
                throw new BpmnExecutionException($"BPMN compensate event '{thrower.ElementId}' targets activityRef '{activityRef}', which does not exist in this process.") { ElementIds = [thrower.ElementId, activityRef] };
            if (!compensationHosts.Contains(activityRef))
                throw new BpmnExecutionException($"BPMN compensate event '{thrower.ElementId}' targets activityRef '{activityRef}', which has no attached compensation boundary.") { ElementIds = [thrower.ElementId, activityRef] };
        }
    }

    /// <summary>
    /// Transaction rules. An <c>IsTransaction</c> element must be a <c>subProcess</c>-family element
    /// with bound work and must not carry loop characteristics (multi-instance transactions are not supported). A cancel end
    /// event is valid only inside a transaction structure. A cancel boundary may only attach to an
    /// <c>IsTransaction</c> host, and a transaction host carries at most one cancel boundary. Each rule rejects
    /// deterministically with the offending element named.
    /// </summary>
    private static void ValidateTransaction(IReadOnlyCollection<BpmnElement> elements, bool structureIsTransaction)
    {
        var elementsById = elements.ToDictionary(element => element.ElementId, StringComparer.Ordinal);

        // Rule 1 / Rule 4 — an IsTransaction element is a subProcess with bound work and no loop characteristics.
        foreach (var element in elements.Where(element => element.IsTransaction))
        {
            if (!StringComparer.Ordinal.Equals(element.ElementType, BpmnElementTypes.SubProcess))
                throw new BpmnExecutionException($"BPMN element '{element.ElementId}' ({element.ElementType}) is marked as a transaction, but only a subprocess element may be a transaction.") { ElementIds = [element.ElementId] };
            if (element.BindingRef is null)
                throw new BpmnExecutionException($"BPMN transaction '{element.ElementId}' requires bound work (the nested transaction process).") { ElementIds = [element.ElementId] };
            if (element.LoopCharacteristics is not null)
                throw new BpmnExecutionException($"BPMN transaction '{element.ElementId}' cannot carry multi-instance loop characteristics; multi-instance transactions are not supported.") { ElementIds = [element.ElementId] };
        }

        // Rule 2 — a cancel end event is valid only inside a transaction structure.
        foreach (var cancelEnd in elements.Where(BpmnElementFamilies.IsCancelEndEvent))
        {
            if (!structureIsTransaction)
                throw new BpmnExecutionException($"BPMN cancel end event '{cancelEnd.ElementId}' is only valid inside a transaction; this process is not a transaction.") { ElementIds = [cancelEnd.ElementId] };
        }

        // Rule 2/3 — a cancel boundary attaches only to a transaction host, at most one per host.
        var cancelBoundaryCountByHost = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var boundary in elements.Where(BpmnElementFamilies.IsCancelBoundary))
        {
            // ValidateBoundaryEvents already rejects a missing/unresolvable host, so a resolvable host is expected here.
            if (boundary.AttachedToRef is not { } hostId || !elementsById.TryGetValue(hostId, out var host))
                continue;
            if (!host.IsTransaction)
                throw new BpmnExecutionException($"BPMN cancel boundary event '{boundary.ElementId}' is attached to '{hostId}', which is not a transaction; a cancel boundary may only attach to a transaction.") { ElementIds = [boundary.ElementId, hostId] };

            cancelBoundaryCountByHost[hostId] = (cancelBoundaryCountByHost.TryGetValue(hostId, out var count) ? count : 0) + 1;
            if (cancelBoundaryCountByHost[hostId] > 1)
                throw new BpmnExecutionException($"BPMN transaction '{hostId}' declares more than one cancel boundary event; a transaction host may carry at most one.") { ElementIds = [hostId] };
        }
    }

    private static bool IsCompensateThrowOrEnd(BpmnElement element) =>
        (StringComparer.Ordinal.Equals(element.ElementType, BpmnElementTypes.IntermediateThrowEvent) ||
         StringComparer.Ordinal.Equals(element.ElementType, BpmnElementTypes.EndEvent)) &&
        BpmnElementFamilies.HasCompensateDefinition(element);

    /// <summary>
    /// Refuses a condition expression on a flow out of an element that takes every outgoing flow without
    /// testing a condition: a start event or a parallel gateway (an event-based gateway is refused by its own
    /// rule first). Nothing would ever evaluate the expression, so running it would take a flow its author
    /// made conditional, which is incorrect execution rather than reduced execution (ADRs 0004 and 0011). The
    /// refusal is structural and fires whatever the host declares. A flow with a <c>conditionOutcome</c> is
    /// left as it was, expression or not: the outcome decides such a flow, and these elements have always
    /// passed over outcomes.
    /// </summary>
    private static void RefuseExpressionsNothingEvaluates(
        IReadOnlyCollection<BpmnElement> elements,
        ILookup<string, BpmnSequenceFlow> outboundBySource)
    {
        foreach (var element in elements.Where(BpmnElementFamilies.TakesEveryOutgoingFlow))
        {
            var kind = element.ElementType switch
            {
                BpmnElementTypes.StartEvent => "start event",
                BpmnElementTypes.ParallelGateway => "parallel gateway",
                _ => "event-based gateway"
            };
            foreach (var flow in outboundBySource[element.ElementId].Where(BpmnFlowSelector.IsExpressionOnly))
                throw new BpmnExecutionException(
                    $"BPMN {kind} '{element.ElementId}' outbound flow '{flow.FlowId}' cannot carry a condition expression; a {kind} takes every outgoing flow and never evaluates one.") { ElementIds = [element.ElementId, flow.FlowId] };
        }
    }

    /// <summary>
    /// Event-based gateway rules: at least two outbound flows; every outbound flow targets an
    /// intermediate catch event whose only inbound flow is this gateway's; the gateway's outbound flows carry
    /// no outcome condition, no condition expression and no default (the race is decided by stimulus arrival,
    /// not by a condition, so an authored condition/default is rejected rather than silently ignored).
    /// </summary>
    private static void ValidateEventBasedGateways(
        IReadOnlyCollection<BpmnElement> elements,
        ILookup<string, BpmnSequenceFlow> outboundBySource,
        ILookup<string, BpmnSequenceFlow> inboundByTarget)
    {
        var elementsById = elements.ToDictionary(element => element.ElementId, StringComparer.Ordinal);

        foreach (var gateway in elements.Where(element => StringComparer.Ordinal.Equals(element.ElementType, BpmnElementTypes.EventBasedGateway)))
        {
            var outbound = outboundBySource[gateway.ElementId].ToArray();
            if (outbound.Length < 2)
                throw new BpmnExecutionException($"BPMN event-based gateway '{gateway.ElementId}' must have at least two outbound sequence flows (a race needs at least two catch events); it has {outbound.Length}.") { ElementIds = [gateway.ElementId] };

            if (gateway.DefaultFlowId is not null)
                throw new BpmnExecutionException($"BPMN event-based gateway '{gateway.ElementId}' cannot declare a default sequence flow.") { ElementIds = [gateway.ElementId] };

            foreach (var flow in outbound)
            {
                if (flow.ConditionOutcome is not null || flow.ConditionExpression is not null || flow.IsDefault)
                    throw new BpmnExecutionException($"BPMN event-based gateway '{gateway.ElementId}' outbound flow '{flow.FlowId}' cannot carry a condition or be a default flow; the race is decided by stimulus arrival.") { ElementIds = [gateway.ElementId, flow.FlowId] };

                if (!elementsById.TryGetValue(flow.TargetRef, out var target) ||
                    !StringComparer.Ordinal.Equals(target.ElementType, BpmnElementTypes.IntermediateCatchEvent))
                    throw new BpmnExecutionException($"BPMN event-based gateway '{gateway.ElementId}' outbound flow '{flow.FlowId}' must target an intermediate catch event; it targets '{flow.TargetRef}'.") { ElementIds = [gateway.ElementId, flow.FlowId, flow.TargetRef] };

                var targetInbound = inboundByTarget[flow.TargetRef].Count();
                if (targetInbound != 1)
                    throw new BpmnExecutionException($"BPMN event-based gateway '{gateway.ElementId}' targets catch event '{flow.TargetRef}', which must have exactly one inbound flow (the gateway); it has {targetInbound}.") { ElementIds = [gateway.ElementId, flow.TargetRef] };
            }
        }
    }

}

/// <summary>The trigger kind of an event subprocess: the two dormant-catcher triggers (escalation/error) plus the three tier-2 external-trigger listeners (message/signal/timer).</summary>
public enum BpmnEventSubprocessTriggerKind
{
    /// <summary>An escalation-triggered event subprocess (interrupting or non-interrupting), matched by code (exact beats code-less catch-all).</summary>
    Escalation,

    /// <summary>An error-triggered event subprocess (interrupting only, per BPMN), matched by code (exact beats code-less catch-all).</summary>
    Error,

    /// <summary>A message-triggered event subprocess: armed as a scope listener at scope start, fired by a named-event stimulus.</summary>
    Message,

    /// <summary>A signal-triggered event subprocess: armed as a scope listener at scope start, fired by a named-event stimulus.</summary>
    Signal,

    /// <summary>
    /// A timer-triggered event subprocess: armed as a scope listener at scope start (a one-shot <c>Delay</c>). A duration
    /// or cycle timer is re-armed per non-interrupting fire for repetition, a counted cycle until it has fired its
    /// <see cref="BpmnEventSubprocessCatcher.Repetitions"/>; a date timer fires once (see <see cref="BpmnEventSubprocessCatcher.FiresOnce"/>).
    /// </summary>
    Timer
}

/// <summary>
/// A graph-derived event-subprocess catcher: a scope's <c>TriggeredByEvent</c> subprocess indexed by
/// its body start-event trigger. Escalation/error catchers are dormant (no arming — resolved from the graph when their
/// signal occurs); message/signal/timer catchers carry a <see cref="ListenerBindingRef"/> and are armed as scope listeners
/// at scope start. <see cref="BodyStartElementId"/> is the single event-start element the body is seeded from
/// via the scheduled-start hint; <see cref="Interrupting"/> is the body start event's <c>isInterrupting</c> flag.
/// </summary>
public sealed record BpmnEventSubprocessCatcher(
    string ElementId,
    string BindingRef,
    string BodyStartElementId,
    BpmnEventSubprocessTriggerKind TriggerKind,
    string? Code,
    bool Interrupting,
    string? ListenerBindingRef = null)
{
    /// <summary>
    /// True when the listener fires at most once, so a non-interrupting fire does not re-arm it: a timer at a date,
    /// whose instant has passed once it fired. Re-arming it would fire again at once, without end.
    /// </summary>
    public bool FiresOnce { get; init; }

    /// <summary>
    /// How many times a timer on an <c>R&lt;n&gt;/</c> cycle fires, n, or <c>null</c> when nothing bounds the listener: a
    /// cycle without a count, a duration, or a message or signal trigger. A non-interrupting listener is not re-armed once
    /// it has fired this many times, counted by <see cref="Bpmn.Model.State.BpmnToken.FireCount"/>; an interrupting one
    /// fires once whatever the count.
    /// </summary>
    public int? Repetitions { get; init; }

    /// <summary>True when this catcher is an external-trigger listener (message/signal/timer): armed at scope start with the suspending work named by <see cref="ListenerBindingRef"/>. Escalation and error catchers are dormant.</summary>
    public bool IsExternalTrigger =>
        TriggerKind is BpmnEventSubprocessTriggerKind.Message or BpmnEventSubprocessTriggerKind.Signal or BpmnEventSubprocessTriggerKind.Timer;
}

/// <summary>
/// One unit of host work bound to a BPMN element, named by the opaque
/// <see cref="BpmnElement.BindingRef"/> (or <see cref="BpmnElement.ListenerBindingRef"/>) the element declares.
/// The interpreter never inspects what the work is; it only asks the host to start it.
/// </summary>
/// <param name="BindingRef">The binding key. Must match exactly one element's declared binding.</param>
/// <param name="NestedProcess">
/// The process definition this work runs, when the work is a nested BPMN process. Required for an event
/// subprocess body, whose start-event trigger the graph validator reads; <c>null</c> for every other kind of
/// work, including subprocesses whose body the host resolves on its own.
/// </param>
public sealed record BpmnBoundWork(string BindingRef, BpmnProcessDefinition? NestedProcess = null)
{
    /// <summary>
    /// The bound-work set <paramref name="definition"/> declares, ready to hand to
    /// <see cref="BpmnGraph.Build"/>.
    /// <para>
    /// <see cref="BpmnGraph.Build"/> demands one entry per declared binding, no more and no less. That is
    /// the right contract for a host wiring real implementations to bindings, and pure ceremony for a host
    /// whose implementations are "wait until someone tells me". Without this, every host writes the same
    /// walk over elements collecting <see cref="BpmnElement.BindingRef"/> and
    /// <see cref="BpmnElement.ListenerBindingRef"/>, and gets the nested-process re-attachment subtly wrong.
    /// </para>
    /// </summary>
    /// <param name="definition">The process whose bindings to collect.</param>
    /// <param name="nestedProcess">
    /// Resolves the nested definition behind a binding that runs one. Required for an event subprocess body,
    /// whose start-event trigger the graph validator reads out of it; return <c>null</c> for anything else.
    /// </param>
    public static IReadOnlyCollection<BpmnBoundWork> Derive(
        BpmnProcessDefinition definition,
        Func<string, BpmnProcessDefinition?>? nestedProcess = null)
    {
        if (definition is null) throw new ArgumentNullException(nameof(definition));

        var bindings = new List<BpmnBoundWork>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var element in definition.Elements)
        {
            // Only the primary binding can run a nested process; a scope listener is always a plain wait.
            Add(element.BindingRef, canNest: true);
            Add(element.ListenerBindingRef, canNest: false);
        }

        return bindings;

        void Add(string? bindingRef, bool canNest)
        {
            if (bindingRef is null || !seen.Add(bindingRef)) return;
            bindings.Add(new BpmnBoundWork(bindingRef, canNest ? nestedProcess?.Invoke(bindingRef) : null));
        }
    }
}
