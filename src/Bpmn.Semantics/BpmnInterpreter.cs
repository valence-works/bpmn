using System.Text.Json;
using Bpmn.Model;
using Bpmn.Model.State;
using Bpmn.Semantics.Behaviors;
using static Bpmn.Semantics.BpmnStateMutator;

namespace Bpmn.Semantics;

/// <summary>
/// The BPMN 2.0 token-semantics interpreter: a pure, synchronous state transition function over a process
/// graph. Each entry point takes the prior state plus a snapshot of the host's world and returns the next
/// state, what this process scope should do next, and the commands the host must carry out. It performs no
/// I/O, reads no clock, holds no state between calls, and never calls back into the host.
/// <para>
/// The interpreter owns dispatch decisions and the token propagation loop, and delegates join accounting to
/// <see cref="BpmnTokenCoordinator"/> and behavior resolution to <see cref="IBpmnBehaviorRegistry"/>. Every
/// record id is a pure function of <see cref="BpmnExecutionState.Sequence"/>, whose only mutation home is
/// <see cref="BpmnStateMutator"/>, so replaying the same inputs produces byte-identical state.
/// </para>
/// </summary>
public sealed class BpmnInterpreter
{
    private readonly IBpmnBehaviorRegistry _behaviors;
    private readonly BpmnTokenCoordinator _tokenCoordinator = new();

    /// <summary>Creates an interpreter dispatching through <paramref name="behaviors"/>.</summary>
    public BpmnInterpreter(IBpmnBehaviorRegistry behaviors)
    {
        ArgumentNullException.ThrowIfNull(behaviors);
        _behaviors = behaviors;
    }

    /// <summary>An interpreter carrying the built-in behavior for every element family. No container required.</summary>
    public static BpmnInterpreter CreateDefault() => new(BpmnBehaviorRegistry.CreateDefault());

    /// <summary>The outcome a process completes with normally.</summary>
    public const string DoneOutcomeName = "Done";

    /// <summary>The correlation key carrying the token parked while a unit of work runs.</summary>
    public const string TokenIdCorrelationKey = "bpmn.tokenId";

    /// <summary>The correlation key carrying the element a unit of work belongs to.</summary>
    public const string ElementIdCorrelationKey = "bpmn.elementId";

    /// <summary>The correlation key carrying why a unit of work was started.</summary>
    public const string SchedulingCauseCorrelationKey = "bpmn.schedulingCause";

    /// <summary>The correlation key carrying the binding ref of the started work.</summary>
    public const string BindingRefCorrelationKey = "bpmn.bindingRef";

    /// <summary>
    /// The correlation key carrying the start event a nested process must begin at. Set only on an event
    /// subprocess body's start, and read back only under
    /// <see cref="EventSubprocessBodySchedulingCause"/>, so an inherited hint can never contaminate an
    /// ordinary nested process's seeding.
    /// </summary>
    public const string StartElementIdCorrelationKey = "bpmn.startElementId";

    /// <summary>The teardown reason recorded on a losing event-based-gateway catch when the first catch won.</summary>
    public const string EventGatewayRaceCancellationReason = "bpmn.event-based-gateway.superseded-by-first-catch";

    /// <summary>The teardown reason recorded on a boundary catch listener when its host completed first.</summary>
    public const string BoundarySupersededByHostCompletionReason = "bpmn.boundary.superseded-by-host-completion";

    /// <summary>The teardown reason recorded on the host (and its sibling listeners) when an interrupting boundary event — catch or error — fired.</summary>
    public const string BoundaryHostInterruptedReason = "bpmn.boundary.host-interrupted";

    /// <summary>The scheduling cause recorded when a multi-instance loop starts one instance.</summary>
    public const string MultiInstanceSchedulingCause = "multi-instance";

    /// <summary>The scheduling cause recorded when a compensation handler runs.</summary>
    public const string CompensationSchedulingCause = "compensation";

    /// <summary>The teardown reason recorded on a compensation run's handler when its coordinating throw token was cancelled mid-replay.</summary>
    public const string CompensationRunCancelledReason = "bpmn.compensation.run-cancelled";

    /// <summary>The reason recorded on the other live tokens a cancel end event stops when it begins cancelling a transaction scope.</summary>
    public const string TransactionCancelledStopReason = "bpmn.transaction.cancel-stopped-live-work";

    /// <summary>The distinguishable outcome a cancelled transaction completes with; an enclosing scope maps it to the attached cancel boundary event.</summary>
    public const string CancelledOutcomeName = "Cancelled";

    /// <summary>
    /// The single signal code the interpreter raises for every escalation. The escalation identity travels in
    /// the payload, so a host multiplexing several kinds of scope signal keeps one reserved code rather than a
    /// growing namespace.
    /// </summary>
    public const string EscalationSignalCode = "bpmn.escalation";

    /// <summary>The teardown reason recorded on the host when an interrupting escalation boundary event fired.</summary>
    public const string EscalationHostInterruptedReason = "bpmn.escalation.host-interrupted";

    /// <summary>The scheduling cause recorded when an event subprocess body starts; it also gates the start-element hint.</summary>
    public const string EventSubprocessBodySchedulingCause = "bpmn.event-subprocess.body";

    /// <summary>The reason recorded on the other live tokens an interrupting event subprocess stops when it interrupts its scope.</summary>
    public const string EventSubprocessScopeInterruptedReason = "bpmn.event-subprocess.scope-interrupted";

    /// <summary>The scheduling cause recorded when a scope listener is armed, at scope start and again after each non-interrupting fire.</summary>
    public const string ScopeListenerSchedulingCause = "bpmn.event-subprocess.listener";

    /// <summary>The teardown reason recorded on a still-armed scope listener when its scope completed and retired it.</summary>
    public const string EventSubprocessListenerSupersededByCompletionReason = "bpmn.event-subprocess.listener-superseded-by-completion";

    /// <summary>The fault code raised when a transaction completes Cancelled but no cancel boundary event is attached to route the cancellation.</summary>
    public const string TransactionCancelledUnhandledFaultCode = "bpmn.transaction.cancelled-unhandled";

    /// <summary>The teardown reason recorded when a call activity's failure outcome routed a catcher or faulted.</summary>
    public const string CallActivityFailureRoutedReason = "bpmn.call-activity.failure-routed";

    /// <summary>The outcome name a call activity's bound work reports when the called process itself faulted.</summary>
    public const string CallActivityFaultedOutcomeName = "Faulted";

    /// <summary>The outcome name a call activity's bound work reports when the called process could not be started.</summary>
    public const string CallActivityDispatchFailedOutcomeName = "DispatchFailed";

    /// <summary>The outcome name a call activity's bound work reports when the called process was cancelled. Shares the value of <see cref="CancelledOutcomeName"/>.</summary>
    public const string CallActivityCancelledOutcomeName = "Cancelled";

    /// <summary>The fault code raised when a call activity's <c>Faulted</c> outcome reaches no error catcher.</summary>
    public const string CallActivityFaultedFaultCode = "bpmn.call-activity.faulted";

    /// <summary>The fault code raised when a call activity's <c>DispatchFailed</c> outcome reaches no error catcher.</summary>
    public const string CallActivityDispatchFailedFaultCode = "bpmn.call-activity.dispatch-failed";

    /// <summary>The fault code raised when a call activity's <c>Cancelled</c> outcome reaches no error catcher.</summary>
    public const string CallActivityCancelledFaultCode = "bpmn.call-activity.cancelled";

    /// <summary>
    /// The call activity failure outcomes the interpreter translates into BPMN error handling, in precedence
    /// order. Every other outcome routes as ordinary task flow.
    /// </summary>
    private static readonly IReadOnlyList<string> CallActivityFailureOutcomes =
        [CallActivityFaultedOutcomeName, CallActivityDispatchFailedOutcomeName, CallActivityCancelledOutcomeName];

    /// <summary>
    /// An empty live-work map. An own-scope escalation activation stops other live work logically only: work
    /// already running keeps running and its late completion is absorbed by the cancelled-token guard, so no
    /// teardown command is issued.
    /// </summary>
    private static readonly IReadOnlyDictionary<(string BindingRef, string? IterationId), string> NoLiveWork =
        new Dictionary<(string BindingRef, string? IterationId), string>();

    /// <summary>The prefix of a multi-instance instance's minted iteration id; the instance token id it embeds is unique across the process.</summary>
    public const string MultiInstanceIterationIdPrefix = "bpmn-mi:";

    /// <summary>The fault code raised when a collection-mode multi-instance activity's collection variable is not readable at all.</summary>
    public const string CollectionUnreadableFaultCode = "bpmn.loop.collection-unreadable";

    /// <summary>The fault code raised when a collection-mode multi-instance activity's collection variable holds a present, non-array value.</summary>
    public const string CollectionNotACollectionFaultCode = "bpmn.loop.collection-not-a-collection";

    /// <summary>The fault code raised when a collection-mode multi-instance activity's collection variable is held outside the inline payload and cannot be read.</summary>
    public const string CollectionNotInlineFaultCode = "bpmn.loop.collection-not-inline";

    /// <summary>
    /// Starts a process instance. Seeds the start token — from the external trigger the host resolved, from the
    /// start-element hint an event subprocess body carries, or, on direct invocation, one token per none start
    /// event — arms the scope's event-subprocess listeners, and propagates until quiescent.
    /// </summary>
    public BpmnEvaluation Start(BpmnStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var graph = request.Graph;
        var context = new BpmnEvaluationContext(request.Host);
        var state = request.PriorState ?? new BpmnExecutionState();

        if (graph.Elements.Count == 0)
            return new BpmnEvaluation(state, new BpmnContinuation.Complete(DoneOutcomeName), context.Commands.ToArray());

        // An external trigger names exactly one event-defined start event. An event subprocess body carries a
        // start-element hint (gated on its scheduling cause), so exactly its single event-start element seeds.
        // Otherwise this is a direct invocation and every none start event seeds a token.
        var seedResult = request.StartSignal is { } startSignal
            ? SeedFromStartSignal(graph, state, startSignal)
            : TryReadStartElementHint(context) is { } hintedStartElementId
                ? SeedFromStartElementHint(graph, state, hintedStartElementId)
                : SeedFromDirectInvocation(graph, state);

        if (seedResult.Fault is not null)
            return FinishEvaluation(context, seedResult);

        // Arm one scope listener per external-trigger (message/signal/timer) event subprocess, in deterministic
        // element-id ordinal order. Arming is two-phase. The listener TOKENS are minted BEFORE propagating the
        // seed, so an interrupting activation raised by the seed's own propagation (an own-scope escalation)
        // drains them as ordinary live tokens. Their suspending work is started AFTER propagation, and only when
        // real work remains live: a terminal evaluation may not also start work, so a scope whose seed completes
        // synchronously (start straight to end) would otherwise strand just-started listener work against the
        // same evaluation's completion. Deferring the start lets such a scope complete with the listener token
        // retired and no work ever started, which is what BPMN says — the listener never got a chance to fire.
        // Every seeding path arms: a nested event-subprocess body arms its OWN nested listeners.
        var scopeInstanceId = context.Host.ScopeInstanceId;
        var armedState = MintScopeListenerTokens(graph, seedResult.State, scopeInstanceId);

        var result = Propagate(context, graph, armedState, scopeInstanceId);
        if (result is { Fault: null, Terminated: false } && result.State.ActiveWork.Count > 0)
            result = result with { State = StartScopeListenerWork(context, graph, result.State, scopeInstanceId) };

        return FinishEvaluation(context, result);
    }

    /// <summary>Seeds the start event an external trigger matched; a start element that is not event-defined faults deterministically.</summary>
    private static EvaluationResult SeedFromStartSignal(BpmnGraph graph, BpmnExecutionState state, BpmnStartSignal startSignal)
    {
        var startElement = graph.StartEvents.FirstOrDefault(element =>
            StringComparer.Ordinal.Equals(element.ElementId, startSignal.StartElementId) && element.EventDefinitions.Count > 0);
        if (startElement is null)
        {
            var message = $"BPMN process was started by a trigger targeting start element '{startSignal.StartElementId}', which is not an event-defined start event of this process.";
            state = BpmnDiagnosticAccumulator.Add(state, BpmnDiagnosticKind.Faulted, null, null, null, message);
            return new EvaluationResult(state, new BpmnFault("bpmn.start.unresolved-trigger", message));
        }

        return SeedToken(state, startElement.ElementId, $"BPMN event-defined start event '{startElement.ElementId}' emitted the initial token from a trigger.");
    }

    /// <summary>
    /// Reads the event-subprocess body start-element hint from the invocation correlation, gated on the
    /// event-subprocess-body scheduling cause so an inherited hint on an ordinary nested process's invocation
    /// (whose cause differs) is ignored. <c>null</c> when absent.
    /// </summary>
    private static string? TryReadStartElementHint(BpmnEvaluationContext context)
    {
        var correlation = context.Host.InvocationCorrelation;
        return correlation.TryGetValue(SchedulingCauseCorrelationKey, out var cause)
               && StringComparer.Ordinal.Equals(cause, EventSubprocessBodySchedulingCause)
               && correlation.TryGetValue(StartElementIdCorrelationKey, out var hint)
               && !string.IsNullOrWhiteSpace(hint)
            ? hint
            : null;
    }

    /// <summary>
    /// Seeds an event-subprocess body from its start-element hint: the single event-start element named by the
    /// hint seeds one token, then the body runs as an ordinary nested process. A hint naming an element that is
    /// not a start event of this process faults deterministically.
    /// </summary>
    private static EvaluationResult SeedFromStartElementHint(BpmnGraph graph, BpmnExecutionState state, string startElementId)
    {
        var startElement = graph.StartEvents.FirstOrDefault(element => StringComparer.Ordinal.Equals(element.ElementId, startElementId));
        if (startElement is null)
        {
            var message = $"BPMN process was started with a start-element hint '{startElementId}', which is not a start event of this process.";
            state = BpmnDiagnosticAccumulator.Add(state, BpmnDiagnosticKind.Faulted, null, null, null, message);
            return new EvaluationResult(state, new BpmnFault("bpmn.start.unresolved-hint", message));
        }

        return SeedToken(state, startElement.ElementId, $"BPMN event-subprocess body start '{startElement.ElementId}' seeded the initial token from a start-element hint.");
    }

    private static EvaluationResult SeedFromDirectInvocation(BpmnGraph graph, BpmnExecutionState state)
    {
        var noneStarts = graph.StartEvents.Where(element => element.EventDefinitions.Count == 0).ToArray();
        if (noneStarts.Length == 0)
        {
            const string message = "BPMN process has no none start event to start on direct invocation; its start events are all event-defined (message/signal/timer) and require a matching stimulus.";
            state = BpmnDiagnosticAccumulator.Add(state, BpmnDiagnosticKind.Faulted, null, null, null, message);
            return new EvaluationResult(state, new BpmnFault("bpmn.start.none-available", message));
        }

        foreach (var startEvent in noneStarts)
            state = SeedToken(state, startEvent.ElementId, $"BPMN start event '{startEvent.ElementId}' emitted the initial token.").State;

        return new EvaluationResult(state);
    }

    private static EvaluationResult SeedToken(BpmnExecutionState state, string elementId, string message)
    {
        var token = NewToken(state, elementId, flowId: null, parentTokenId: null, BpmnTokenStatus.Active, producingWorkHandle: null);
        state = AddToken(state, token);
        state = BpmnDiagnosticAccumulator.Add(state, BpmnDiagnosticKind.TokenEmitted, elementId, null, token.TokenId, message);
        return new EvaluationResult(state);
    }

    /// <summary>
    /// Reports that a started unit of work completed. Resolves the token it was parked on, applies the
    /// interception ladder (multi-instance instance, compensation handler, cancelled transaction, event
    /// subprocess, call activity failure, event-based-gateway race, boundary-event teardown), then dispatches
    /// the element's behavior and propagates.
    /// </summary>
    public BpmnEvaluation OnWorkCompleted(BpmnWorkCompletedRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var graph = request.Graph;
        var context = new BpmnEvaluationContext(request.Host);
        var completionContext = request;
        var state = request.PriorState ?? new BpmnExecutionState();

        var tokenId = ResolveTokenId(context, state, completionContext.CompletedBindingRef, completionContext.CompletedIterationId);
        state = RemoveActiveWork(state, tokenId);
        var token = GetRequiredToken(state, tokenId);

        if (state.Terminated || token.Status == BpmnTokenStatus.Canceled)
        {
            state = BpmnDiagnosticAccumulator.Add(state, BpmnDiagnosticKind.Canceled, token.AtElementId, token.FlowId, token.TokenId, $"BPMN ignored completion for canceled token '{token.TokenId}'.");
            return FinishEvaluation(context, new EvaluationResult(state));
        }

        // The teardowns this evaluation carries, issued only on a non-fault continuation.
        var pendingCancellations = new List<PendingTeardown>();
        var liveWorkHandles = BuildLiveWorkHandles(context);

        // A multi-instance instance completed — advance the loop (start the next instance in sequential mode)
        // or, on the last instance, retire the host's listeners and route the host's outbound flows through its
        // normal behavior. Instances never route per-completion, so this short-circuits the normal behavior
        // dispatch below: the instance token is not a race member, and its coordinator, not the instance, owns
        // any boundary listeners.
        if (token.ParentTokenId is { } instanceParentTokenId
            && FindLoopByCoordinator(state, instanceParentTokenId) is { } loop
            && StringComparer.Ordinal.Equals(token.AtElementId, loop.ElementId))
        {
            //  (MI composition): the instance interception context is entered first; when the instance is a
            // call activity completing with a failure outcome, the call-activity ladder routes here composed with
            // the coordinator cascade: RouteCallActivityFailureOutcome resolves the interrupt target to the loop
            // coordinator, so firing an error boundary or error event subprocess interrupts every remaining instance.
            if (IsCallActivityFailureCompletion(graph, token, completionContext.OutcomeNames, out var instanceFailureOutcome))
            {
                return FinishEvaluation(context, RouteCallActivityFailureOutcome(
                    context, graph, state, token, instanceFailureOutcome, liveWorkHandles, pendingCancellations, completionContext.CompletedHandle));
            }

            return FinishEvaluation(context, HandleMultiInstanceInstanceCompletion(
                context, graph, state, token, loop, liveWorkHandles, pendingCancellations, completionContext.CompletedHandle));
        }

        // a compensation handler sub-token completed — intercept BEFORE behavior dispatch (the MI-instance
        // interception precedent). Consume the sub-token, mark its compensable Compensated, and advance the run:
        // schedule the next claimed handler, or drop the run and complete the coordinating throw token.
        if (token.ParentTokenId is { } handlerParentTokenId
            && FindCompensationRun(state, handlerParentTokenId) is { } compensationRun
            && graph.GetRequiredElement(token.AtElementId).IsForCompensation)
        {
            return FinishEvaluation(context, HandleCompensationHandlerCompletion(
                context, graph, state, token, compensationRun, completionContext.CompletedHandle));
        }

        // A transaction that completed with the Cancelled outcome is intercepted BEFORE normal
        // behavior dispatch and BEFORE Case B compensable registration — a cancelled transaction is not
        // successfully-completed work: it registers no compensable, routes no normal outbound, and instead fires
        // its attached cancel boundary (or faults deterministically when none is attached).
        if (graph.GetRequiredElement(token.AtElementId).IsTransaction
            && completionContext.OutcomeNames.Contains(CancelledOutcomeName, StringComparer.Ordinal))
        {
            return FinishEvaluation(context, HandleTransactionCancelled(
                context, graph, state, token, liveWorkHandles, pendingCancellations, completionContext.CompletedHandle));
        }

        // a completion at a TriggeredByEvent element — intercept BEFORE behavior dispatch (the
        // MI/compensation-handler interception precedent). A listener token and an activation token both sit here, so
        // discriminate on Kind: a Listener completion is a fired scope listener (activate + re-arm/interrupt),
        // before the body-completion check; an Activation (or kind-less) completion is the body finishing —
        // the element has no flows, so the activation token is consumed and nothing is routed.
        if (graph.GetRequiredElement(token.AtElementId).TriggeredByEvent)
        {
            if (token.Kind == BpmnTokenKind.Listener && graph.EventSubprocessByElementId(token.AtElementId) is { } firedCatcher)
            {
                return FinishEvaluation(context, HandleScopeListenerFired(
                    context, graph, state, token, firedCatcher, liveWorkHandles, pendingCancellations, completionContext.CompletedHandle));
            }

            return FinishEvaluation(context, HandleEventSubprocessBodyCompletion(state, token));
        }

        // A call activity's bound work completed with a failure outcome (Faulted/DispatchFailed/Cancelled) —
        // intercept BEFORE normal behavior dispatch, joining the interception ladder above. The completion never
        // routes normal outbound; the interpreter routes the error-catcher ladder directly (host error boundary,
        // then scope error event subprocess, then process fault). The multi-instance case is handled above, inside
        // the instance interception, so the coordinator cascade composes first.
        if (IsCallActivityFailureCompletion(graph, token, completionContext.OutcomeNames, out var callActivityFailureOutcome))
        {
            return FinishEvaluation(context, RouteCallActivityFailureOutcome(
                context, graph, state, token, callActivityFailureOutcome, liveWorkHandles, pendingCancellations, completionContext.CompletedHandle));
        }

        // /D3: if the completing token is a live event-based-gateway race member, it is the winner.
        // Resolve the race logically first (cancel losing member tokens, drop their work records), and carry
        // the losers' teardowns — they are issued later only on a non-fault continuation.
        var race = state.Races.FirstOrDefault(candidate => !candidate.Resolved && candidate.MemberTokenIds.Contains(token.TokenId, StringComparer.Ordinal));
        if (race is not null)
        {
            state = ResolveEventRace(state, race, token.TokenId, liveWorkHandles, pendingCancellations);
            token = GetRequiredToken(state, token.TokenId);
        }

        // apply boundary-event completion semantics before dispatching the completing element's
        // behavior — a host completion tears down its live listeners, an interrupting listener tears down its
        // host and sibling listeners, a non-interrupting listener leaves both untouched. Purely logical here;
        // the teardowns are carried and issued only on a non-fault continuation.
        state = ApplyBoundaryCompletionSemantics(graph, state, token, liveWorkHandles, pendingCancellations);

        var element = graph.GetRequiredElement(token.AtElementId);
        var behavior = _behaviors.GetRequired(BpmnElementFamilies.Resolve(element));
        var behaviorContext = new BpmnBehaviorContext(
            BpmnBehaviorTrigger.WorkCompleted,
            element,
            token,
            graph.OutboundFlows(element.ElementId),
            graph.InboundFlows(element.ElementId),
            completionContext.OutcomeNames,
            state);

        var result = ApplyDecision(context, graph, state, token, element, Execute(behavior, behaviorContext, element), completionContext.CompletedHandle);
        if (result is { Fault: null, Terminated: false })
            result = Propagate(context, graph, result.State, completionContext.CompletedHandle);

        return FinishEvaluation(context, result with { PendingTeardowns = pendingCancellations });
    }

    /// <summary>
    /// Reports that a started unit of work failed terminally, and returns how BPMN handled the error.
    /// <para>
    /// When the failing activity's host carries an attached error boundary event, or the scope declares an
    /// error event subprocess, the error is <b>caught</b>: the host token and its still-armed sibling listeners
    /// are torn down and the catcher's path routes, so the process continues and the disposition reports the
    /// catching element. Otherwise the error is <b>propagated</b>: failed work can never complete, so its token
    /// — and any downstream join that requires it — can no longer proceed, and the process faults
    /// deterministically.
    /// </para>
    /// </summary>
    public BpmnFaultEvaluation OnWorkFaulted(BpmnWorkFaultedRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var context = new BpmnEvaluationContext(request.Host);
        var result = EvaluateWorkFaulted(context, request);
        var evaluation = FinishEvaluation(context, result);

        // A caught error is only genuinely caught when the evaluation exits cleanly. If routing the catcher's
        // path itself faults, the whole scope is going down, so the error propagates after all — the same rule
        // that discards carried-but-unissued teardown on a fault continuation.
        var disposition = evaluation.Continuation is BpmnContinuation.Fault
            ? BpmnErrorDisposition.Propagated.Instance
            : result.ErrorDisposition ?? BpmnErrorDisposition.Propagated.Instance;

        return new BpmnFaultEvaluation(evaluation.State, evaluation.Continuation, evaluation.Commands, disposition);
    }

    private EvaluationResult EvaluateWorkFaulted(BpmnEvaluationContext context, BpmnWorkFaultedRequest request)
    {
        var graph = request.Graph;
        var state = request.PriorState ?? new BpmnExecutionState();

        var faultedBindingRef = request.FaultedBindingRef;
        var faultedTokenId = context.Host.InvocationCorrelation.TryGetValue(TokenIdCorrelationKey, out var correlatedTokenId) && !string.IsNullOrWhiteSpace(correlatedTokenId)
            ? correlatedTokenId
            : null;
        var faultedWork = faultedTokenId is not null
            ? state.ActiveWork.FirstOrDefault(work => StringComparer.Ordinal.Equals(work.TokenId, faultedTokenId))
            : state.ActiveWork.FirstOrDefault(work => StringComparer.Ordinal.Equals(work.NodeId, faultedBindingRef));

        // A late fault of work whose token was already cancelled — a sibling multi-instance instance torn down
        // by a catcher or a boundary interrupt, say — or a fault after the process ended, is absorbed by the
        // token-status guard: the teardown already rode the interrupting evaluation, so the process must not
        // fault a second time. This mirrors the completion path's cancelled-token guard.
        var faultedToken = faultedTokenId is not null
            ? state.Tokens.FirstOrDefault(candidate => StringComparer.Ordinal.Equals(candidate.TokenId, faultedTokenId))
            : null;
        if (state.Terminated || faultedToken is { Status: BpmnTokenStatus.Consumed or BpmnTokenStatus.Canceled })
        {
            if (faultedWork is not null)
                state = RemoveActiveWork(state, faultedWork.TokenId);
            state = BpmnDiagnosticAccumulator.Add(state, BpmnDiagnosticKind.Canceled, faultedWork?.ElementId, null, faultedToken?.TokenId,
                $"BPMN ignored a late fault of work '{faultedBindingRef}' whose token was already cancelled or the process ended.");
            return new EvaluationResult(state);
        }

        // Catch the error through an error boundary event attached to the failing work's host.
        if (faultedWork is not null
            && graph.AttachedErrorBoundary(faultedWork.ElementId) is { } errorBoundary
            && state.Tokens.FirstOrDefault(candidate => StringComparer.Ordinal.Equals(candidate.TokenId, faultedWork.TokenId)) is { } hostToken
            && hostToken.Status is not (BpmnTokenStatus.Consumed or BpmnTokenStatus.Canceled))
        {
            return CatchFaultWithErrorBoundary(context, graph, state, faultedWork, hostToken, errorBoundary, request.FaultedHandle);
        }

        // No host error boundary, but the scope declares an error event subprocess — catch there and activate it
        // interrupting. An event-subprocess body's own fault is NOT self-caught (its host element is the
        // TriggeredByEvent element); it takes the ordinary propagation path.
        if (faultedWork is not null
            && !graph.GetRequiredElement(faultedWork.ElementId).TriggeredByEvent
            && graph.ErrorEventSubprocess() is { } errorEventSubprocess
            && state.Tokens.FirstOrDefault(candidate => StringComparer.Ordinal.Equals(candidate.TokenId, faultedWork.TokenId)) is { } errorHostToken
            && errorHostToken.Status is not (BpmnTokenStatus.Consumed or BpmnTokenStatus.Canceled))
        {
            return CatchFaultWithErrorEventSubprocess(context, graph, state, faultedWork, errorHostToken, errorEventSubprocess, request.FaultedHandle);
        }

        if (faultedWork is not null)
            state = RemoveActiveWork(state, faultedWork.TokenId);

        var detail = string.IsNullOrWhiteSpace(request.FaultMessage) ? "" : $" ({request.FaultMessage})";
        var message = $"BPMN process faulted because the work bound to '{faultedBindingRef}' faulted{detail}: failed work cannot complete, so its token — and any downstream join that requires it — can no longer proceed.";
        state = BpmnDiagnosticAccumulator.Add(state, BpmnDiagnosticKind.Faulted, faultedWork?.ElementId, null, faultedWork?.TokenId, message);
        return new EvaluationResult(state, new BpmnFault("bpmn.work.faulted", message));
    }

    /// <summary>
    /// Catches a work fault with the host's attached error boundary event: drops the failing work's record,
    /// tears down the interrupt target and its sibling catch listeners, mints an <see cref="BpmnTokenStatus.Active"/>
    /// token at the boundary to route its outbound flows, and finishes through the shared quiescence machinery.
    /// When the failing work is a multi-instance instance the interrupt target is the loop coordinator, so
    /// tearing it down cascades to every remaining live instance.
    /// <para>
    /// The failing work is already terminal, so no teardown is issued for it — only its bookkeeping record is
    /// dropped. The teardowns for everything else are carried and issued only at the clean exit.
    /// </para>
    /// </summary>
    private EvaluationResult CatchFaultWithErrorBoundary(
        BpmnEvaluationContext context,
        BpmnGraph graph,
        BpmnExecutionState state,
        BpmnActiveWork faultedWork,
        BpmnToken faultedToken,
        BpmnElement errorBoundary,
        string faultedHandle)
    {
        var pendingCancellations = new List<PendingTeardown>();
        var liveWorkHandles = BuildLiveWorkHandles(context);

        // The interrupt target is the multi-instance loop coordinator when the failing work is an instance,
        // otherwise the failing work's own host token.
        var interruptTokenId = ResolveMultiInstanceCoordinatorTokenId(state, faultedToken) ?? faultedToken.TokenId;

        // The failing work is already terminal; only drop its record.
        state = RemoveActiveWork(state, faultedWork.TokenId);

        // Cancel the interrupt target (a coordinator cascades to all live instances; a plain host token is a
        // pure token flip, its work record having just been dropped) and its still-armed sibling catch listeners.
        state = CancelTokenAndWork(state, interruptTokenId, BoundaryHostInterruptedReason, liveWorkHandles, pendingCancellations,
            (token, reason) => $"BPMN error boundary '{errorBoundary.ElementId}' caught a work fault of host '{faultedToken.AtElementId}' and cancelled token '{token.TokenId}' at '{token.AtElementId}' ({reason}).");
        state = CancelHostListeners(graph, state, interruptTokenId, listenerTokenToSkip: null, BoundaryHostInterruptedReason, liveWorkHandles, pendingCancellations);

        // Mint an active token at the error boundary so its behavior routes the error path; it inherits the
        // interrupt target's loop-iteration key.
        var interruptIterationKey = state.Tokens.FirstOrDefault(candidate => StringComparer.Ordinal.Equals(candidate.TokenId, interruptTokenId))?.IterationKey;
        var errorToken = NewToken(state, errorBoundary.ElementId, flowId: null, parentTokenId: interruptTokenId, BpmnTokenStatus.Active, producingWorkHandle: faultedHandle, iterationKey: interruptIterationKey);
        state = AddToken(state, errorToken);
        state = BpmnDiagnosticAccumulator.Add(state, BpmnDiagnosticKind.TokenEmitted, errorBoundary.ElementId, null, errorToken.TokenId,
            $"BPMN error boundary '{errorBoundary.ElementId}' fired and emitted token '{errorToken.TokenId}' to route the error path.");

        return Propagate(context, graph, state, faultedHandle) with
        {
            PendingTeardowns = pendingCancellations,
            ErrorDisposition = new BpmnErrorDisposition.Caught(errorBoundary.ElementId, ReadErrorCode(errorBoundary))
        };
    }

    /// <summary>
    /// Catches a work fault with the scope's error event subprocess: drops the failing work's record, then
    /// activates the (always interrupting) event subprocess — minting a scope-level activation token and
    /// stopping all other live work in the scope, which tears down the failing host token, its sibling
    /// listeners, and any other branch — before starting the body.
    /// </summary>
    private EvaluationResult CatchFaultWithErrorEventSubprocess(
        BpmnEvaluationContext context,
        BpmnGraph graph,
        BpmnExecutionState state,
        BpmnActiveWork faultedWork,
        BpmnToken faultedToken,
        BpmnEventSubprocessCatcher errorEventSubprocess,
        string faultedHandle)
    {
        var pendingCancellations = new List<PendingTeardown>();
        var liveWorkHandles = BuildLiveWorkHandles(context);

        // The failing work is already terminal; drop its record first so the stop-others below (which cancels
        // the failing host token) does not issue a teardown for work that has already gone.
        var faultIterationKey = faultedToken.IterationKey;
        state = RemoveActiveWork(state, faultedWork.TokenId);

        var activation = ActivateEventSubprocess(context, graph, state, errorEventSubprocess, faultIterationKey,
            liveWorkHandles, pendingCancellations, faultedHandle,
            $"work fault of host '{faultedToken.AtElementId}'");
        state = activation.State;

        return Propagate(context, graph, state, faultedHandle) with
        {
            PendingTeardowns = pendingCancellations,
            ErrorDisposition = new BpmnErrorDisposition.Caught(errorEventSubprocess.ElementId, errorEventSubprocess.Code)
        };
    }

    /// <summary>The error code an error boundary event declares, or <c>null</c> when it catches any error.</summary>
    private static string? ReadErrorCode(BpmnElement errorBoundary) =>
        errorBoundary.EventDefinitions.SingleOrDefault() is { } definition
        && definition.Properties.TryGetValue(BpmnEventDefinitionProperties.Code, out var code)
        && !string.IsNullOrWhiteSpace(code)
            ? code.Trim()
            : null;

    /// <summary>
    /// True when the completing token is a call activity whose bound work completed with a translated failure
    /// outcome: <c>Faulted</c>/<c>DispatchFailed</c>/<c>Cancelled</c>. Yields the matched outcome
    /// (deterministic precedence). <c>Completed</c>/<c>Dispatched</c> return <c>false</c> (normal task-flow routing).
    /// </summary>
    private static bool IsCallActivityFailureCompletion(
        BpmnGraph graph,
        BpmnToken token,
        IReadOnlyCollection<string> outcomeNames,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? failureOutcome)
    {
        failureOutcome = null;
        if (!BpmnElementFamilies.IsCallActivity(graph.GetRequiredElement(token.AtElementId)))
            return false;

        failureOutcome = CallActivityFailureOutcomes.FirstOrDefault(outcome => outcomeNames.Contains(outcome, StringComparer.Ordinal));
        return failureOutcome is not null;
    }

    /// <summary>
    /// Routes a call activity's failure outcome: the completion-path analog of catching a work fault, but with no
    /// fault to catch. A called process that faulted, was cancelled, or could not be started COMPLETES this
    /// activity's bound work with a failure outcome rather than failing it, so the host token is consumed and the
    /// error-catcher ladder is routed directly. The completing work's record was already dropped by the caller.
    /// The ladder is: (1) a host-attached error boundary event mints an <see cref="BpmnTokenStatus.Active"/> token
    /// at the boundary, inheriting the interrupt target's iteration key, and propagates; (2) else the scope's error
    /// event subprocess activates interrupting; (3) else the process faults deterministically with the per-outcome
    /// fault code. The interrupt target is the multi-instance loop coordinator when the completing token is an
    /// instance, so firing a catcher interrupts every remaining instance; otherwise it is the host token itself.
    /// </summary>
    private EvaluationResult RouteCallActivityFailureOutcome(
        BpmnEvaluationContext context,
        BpmnGraph graph,
        BpmnExecutionState state,
        BpmnToken completingToken,
        string failureOutcome,
        IReadOnlyDictionary<(string BindingRef, string? IterationId), string> liveWorkHandles,
        List<PendingTeardown> pendingCancellations,
        string completedHandle)
    {
        var element = graph.GetRequiredElement(completingToken.AtElementId);

        // The interrupt target is the MI loop coordinator when the completing token is an instance (
        // coordinator cascade), otherwise the completing host token itself.
        var interruptTokenId = ResolveMultiInstanceCoordinatorTokenId(state, completingToken) ?? completingToken.TokenId;
        var interruptIterationKey = state.Tokens.FirstOrDefault(candidate => StringComparer.Ordinal.Equals(candidate.TokenId, interruptTokenId))?.IterationKey;

        state = BpmnDiagnosticAccumulator.Add(state, BpmnDiagnosticKind.CallActivityFailureRouted, element.ElementId, null, completingToken.TokenId,
            $"BPMN call activity '{element.ElementId}' completed with the '{failureOutcome}' outcome; routing the call-activity failure path.");

        // Ladder tier 1: a host-attached error boundary fires — mint a token to route its outbound flows.
        if (graph.AttachedErrorBoundary(element.ElementId) is { } errorBoundary)
        {
            state = CancelTokenAndWork(state, interruptTokenId, CallActivityFailureRoutedReason, liveWorkHandles, pendingCancellations,
                (token, reason) => $"BPMN error boundary '{errorBoundary.ElementId}' routed a call-activity '{failureOutcome}' outcome of host '{element.ElementId}' and cancelled token '{token.TokenId}' at '{token.AtElementId}' ({reason}).");
            state = CancelHostListeners(graph, state, interruptTokenId, listenerTokenToSkip: null, CallActivityFailureRoutedReason, liveWorkHandles, pendingCancellations);

            var errorToken = NewToken(state, errorBoundary.ElementId, flowId: null, parentTokenId: interruptTokenId, BpmnTokenStatus.Active, producingWorkHandle: completedHandle, iterationKey: interruptIterationKey);
            state = AddToken(state, errorToken);
            state = BpmnDiagnosticAccumulator.Add(state, BpmnDiagnosticKind.TokenEmitted, errorBoundary.ElementId, null, errorToken.TokenId,
                $"BPMN error boundary '{errorBoundary.ElementId}' fired and emitted token '{errorToken.TokenId}' to route the call-activity failure path.");

            var boundaryResult = Propagate(context, graph, state, completedHandle);
            return boundaryResult with { PendingTeardowns = pendingCancellations };
        }

        // Ladder tier 2: the scope's error event subprocess activates interrupting — stop all other live
        // work (which cancels the interrupt target and its listeners) and schedule the body.
        if (graph.ErrorEventSubprocess() is { } errorEventSubprocess)
        {
            var activation = ActivateEventSubprocess(context, graph, state, errorEventSubprocess, interruptIterationKey,
                liveWorkHandles, pendingCancellations, completedHandle,
                $"call-activity '{failureOutcome}' outcome of host '{element.ElementId}'");
            var subprocessResult = Propagate(context, graph, activation.State, completedHandle);
            return subprocessResult with { PendingTeardowns = pendingCancellations };
        }

        // Ladder tier 3: no catcher — the process faults deterministically with the per-outcome fault code.
        // Consume the interrupt target (coordinator cascade or host token) and its listeners; a fault continuation
        // discards the carried teardowns, because the whole process is going down.
        state = CancelTokenAndWork(state, interruptTokenId, CallActivityFailureRoutedReason, liveWorkHandles, pendingCancellations,
            (token, reason) => $"BPMN call activity '{element.ElementId}' faulted and cancelled token '{token.TokenId}' at '{token.AtElementId}' ({reason}).");
        state = CancelHostListeners(graph, state, interruptTokenId, listenerTokenToSkip: null, CallActivityFailureRoutedReason, liveWorkHandles, pendingCancellations);

        var faultCode = failureOutcome switch
        {
            CallActivityFaultedOutcomeName => CallActivityFaultedFaultCode,
            CallActivityDispatchFailedOutcomeName => CallActivityDispatchFailedFaultCode,
            _ => CallActivityCancelledFaultCode
        };
        var message = $"BPMN call activity '{element.ElementId}' completed with the '{failureOutcome}' outcome, but no error boundary or error event subprocess is available to route it.";
        state = BpmnDiagnosticAccumulator.Add(state, BpmnDiagnosticKind.Faulted, element.ElementId, null, completingToken.TokenId, message);
        return new EvaluationResult(state, new BpmnFault(faultCode, message));
    }

    /// <summary>
    /// Raises an escalation from an escalation throw or end event, on the throwing scope's own evaluation.
    /// First consults the throwing scope's OWN escalation event subprocesses (exact code beats the code-less
    /// catch-all): a match returns that catcher so the caller activates it, instead of signalling outward. That
    /// preserves one-hop bubbling — each scope checks itself before passing the signal on. On no own-scope
    /// match, when this process has an enclosing scope it carries a <see cref="BpmnHostCommand.SignalEnclosingScope"/>
    /// (code <see cref="EscalationSignalCode"/>, payload <c>{ code, name? }</c>) issued at the clean exit; at a
    /// root process it is a no-op with an <c>EscalationUnhandled</c> diagnostic. Never faults. The companion
    /// routing command routes or consumes as usual.
    /// </summary>
    private static (BpmnExecutionState State, BpmnEventSubprocessCatcher? OwnScopeActivation) RaiseEscalation(
        BpmnEvaluationContext context,
        BpmnGraph graph,
        BpmnExecutionState state,
        BpmnToken token,
        BpmnElement element)
    {
        var (code, name) = ReadEscalation(element);

        // Own-scope catch: an escalation event subprocess in the throwing scope catches it before bubbling.
        if (graph.EscalationEventSubprocess(code) is { } catcher)
        {
            state = BpmnDiagnosticAccumulator.Add(state, BpmnDiagnosticKind.EscalationCaught, element.ElementId, null, token.TokenId,
                $"BPMN escalation event '{element.ElementId}' raised escalation code '{code}', caught by own-scope event subprocess '{catcher.ElementId}'.");
            return (state, catcher);
        }

        if (context.Host.HasEnclosingScope)
        {
            context.AddCommand(new BpmnHostCommand.SignalEnclosingScope(EscalationSignalCode, BuildEscalationPayload(code, name)));
            return (BpmnDiagnosticAccumulator.Add(state, BpmnDiagnosticKind.EscalationRaised, element.ElementId, null, token.TokenId,
                $"BPMN escalation event '{element.ElementId}' raised escalation code '{code}' to its enclosing scope."), null);
        }

        return (BpmnDiagnosticAccumulator.Add(state, BpmnDiagnosticKind.EscalationUnhandled, element.ElementId, null, token.TokenId,
            $"BPMN escalation event '{element.ElementId}' raised escalation code '{code}' at a root process; no enclosing scope can catch it (no-op)."), null);
    }

    /// <summary>
    /// Activates an event subprocess: mints a scope-level activation token at the event-subprocess
    /// element (<c>AwaitingChild</c>, <c>ParentTokenId</c> null, inheriting <paramref name="triggerIterationKey"/>
    /// where one exists), and starts the bound body work seeded at its single event-start element via the
    /// start-element hint. An interrupting activation first stops all OTHER live work in the scope through the
    /// shared <see cref="StopOtherLiveWork"/> helper (coordinator cascades and carried teardowns honored per the
    /// caller's live-work map), keeping the activation token alive so the scope survives until the body completes. Non-
    /// interrupting activations leave other scope work untouched: repeated or concurrent activations are
    /// independent, each with its own token and its own body. Does not propagate; the caller drives that.
    /// </summary>
    private EvaluationResult ActivateEventSubprocess(
        BpmnEvaluationContext context,
        BpmnGraph graph,
        BpmnExecutionState state,
        BpmnEventSubprocessCatcher catcher,
        string? triggerIterationKey,
        IReadOnlyDictionary<(string BindingRef, string? IterationId), string> liveWorkHandles,
        List<PendingTeardown> pendingCancellations,
        string producedBy,
        string triggerDescription)
    {
        var activationToken = NewToken(state, catcher.ElementId, flowId: null, parentTokenId: null, BpmnTokenStatus.AwaitingChild,
            producingWorkHandle: producedBy, iterationKey: triggerIterationKey, kind: BpmnTokenKind.Activation);
        state = AddToken(state, activationToken);
        state = BpmnDiagnosticAccumulator.Add(state, BpmnDiagnosticKind.EventSubprocessActivated, catcher.ElementId, null, activationToken.TokenId,
            $"BPMN {(catcher.Interrupting ? "interrupting" : "non-interrupting")} event subprocess '{catcher.ElementId}' activated ({triggerDescription}).");

        // Interrupting: stop all OTHER live work in the scope, keeping the activation token alive as the scope's work.
        if (catcher.Interrupting)
            state = StopOtherLiveWork(state, activationToken.TokenId, EventSubprocessScopeInterruptedReason, liveWorkHandles, pendingCancellations,
                (stoppedToken, reason) => $"BPMN event subprocess '{catcher.ElementId}' interrupted the scope and cancelled token '{stoppedToken.TokenId}' at '{stoppedToken.AtElementId}' ({reason}).").State;

        state = StartWork(context, state, catcher.BindingRef, catcher.ElementId, activationToken.TokenId,
            EventSubprocessBodySchedulingCause, startElementId: catcher.BodyStartElementId);
        state = BpmnDiagnosticAccumulator.Add(state, BpmnDiagnosticKind.Scheduled, catcher.ElementId, null, activationToken.TokenId,
            $"BPMN event subprocess '{catcher.ElementId}' started body '{catcher.BindingRef}' seeded at start element '{catcher.BodyStartElementId}'.");

        return new EvaluationResult(state, PendingTeardowns: pendingCancellations);
    }

    /// <summary>
    /// Handles an event-subprocess body completion, intercepted before behavior dispatch. The
    /// event-subprocess element has no outbound flows, so the activation token is simply consumed and nothing is
    /// routed; the scope's ordinary liveness/completion accounting then proceeds.
    /// </summary>
    private static EvaluationResult HandleEventSubprocessBodyCompletion(BpmnExecutionState state, BpmnToken activationToken)
    {
        state = UpdateToken(state, activationToken with { Status = BpmnTokenStatus.Consumed });
        state = BpmnDiagnosticAccumulator.Add(state, BpmnDiagnosticKind.EventSubprocessCompleted, activationToken.AtElementId, null, activationToken.TokenId,
            $"BPMN event subprocess '{activationToken.AtElementId}' body completed; the activation token is consumed and nothing is routed.");
        return new EvaluationResult(state);
    }

    /// <summary>
    /// Handles a fired scope listener, intercepted before behavior dispatch. The listener work
    /// (a suspending <c>Event</c>/<c>Delay</c>) resumed on its stimulus, so the event subprocess activates. The firing
    /// listener token is consumed (its work record was already dropped at the top of <see cref="OnWorkCompleted"/>).
    /// A <b>non-interrupting</b> catcher re-arms a fresh listener FIRST (deterministic ids from <c>Sequence</c>; timer
    /// repetition falls out of the re-arm loop, each arm a single one-shot timer) and then activates the body
    /// non-interrupting alongside untouched scope work. An <b>interrupting</b> catcher does not re-arm: the activation's
    /// <see cref="StopOtherLiveWork"/> drains every other live token — including sibling scope listeners (ordinary live
    /// tokens there, their suspended work riding the carried teardowns) — before starting the body.
    /// </summary>
    private EvaluationResult HandleScopeListenerFired(
        BpmnEvaluationContext context,
        BpmnGraph graph,
        BpmnExecutionState state,
        BpmnToken listenerToken,
        BpmnEventSubprocessCatcher catcher,
        IReadOnlyDictionary<(string BindingRef, string? IterationId), string> liveWorkHandles,
        List<PendingTeardown> pendingCancellations,
        string completedHandle)
    {
        var scopeInstanceId = context.Host.ScopeInstanceId;

        state = UpdateToken(state, listenerToken with { Status = BpmnTokenStatus.Consumed });
        state = BpmnDiagnosticAccumulator.Add(state, BpmnDiagnosticKind.ScopeListenerFired, catcher.ElementId, null, listenerToken.TokenId,
            $"BPMN {(catcher.Interrupting ? "interrupting" : "non-interrupting")} {catcher.TriggerKind} scope listener for event subprocess '{catcher.ElementId}' fired.");

        if (!catcher.Interrupting)
            state = ArmScopeListener(context, state, catcher, scopeInstanceId);

        var activation = ActivateEventSubprocess(context, graph, state, catcher, listenerToken.IterationKey,
            liveWorkHandles, pendingCancellations, completedHandle, $"scope listener '{catcher.ElementId}' fired");
        state = activation.State;

        var result = Propagate(context, graph, state, scopeInstanceId);
        return result with { PendingTeardowns = pendingCancellations };
    }

    /// <summary>
    /// Phase 1 of scope-listener arming: mints one <see cref="BpmnTokenKind.Listener"/> token per
    /// external-trigger (message/signal/timer) event subprocess at its element (<c>AwaitingChild</c>,
    /// <c>ParentTokenId</c> null), in deterministic element-id ordinal order, BEFORE the seed propagates — so an
    /// interrupting activation raised during that propagation drains the listener tokens as ordinary live tokens. Their
    /// suspending work is started later (<see cref="StartScopeListenerWork"/>).
    /// </summary>
    private static BpmnExecutionState MintScopeListenerTokens(BpmnGraph graph, BpmnExecutionState state, string producedBy)
    {
        foreach (var catcher in graph.ExternalTriggerEventSubprocesses)
        {
            var listenerToken = NewToken(state, catcher.ElementId, flowId: null, parentTokenId: null, BpmnTokenStatus.AwaitingChild,
                producingWorkHandle: producedBy, iterationKey: null, kind: BpmnTokenKind.Listener);
            state = AddToken(state, listenerToken);
        }

        return state;
    }

    /// <summary>
    /// Phase 2 of scope-listener arming: starts the suspending listener work for each still-live listener token
    /// that has none yet, in deterministic element-id ordinal order. Called AFTER the seed propagates and only
    /// when real work remains running, so a terminal evaluation never also starts work. A listener token
    /// drained by an interrupting activation during propagation is no longer live and is skipped.
    /// </summary>
    private static BpmnExecutionState StartScopeListenerWork(
        BpmnEvaluationContext context,
        BpmnGraph graph,
        BpmnExecutionState state,
        string producedBy)
    {
        var pendingListenerTokens = state.Tokens
            .Where(token => token.Kind == BpmnTokenKind.Listener && token.Status == BpmnTokenStatus.AwaitingChild
                            && !state.ActiveWork.Any(work => StringComparer.Ordinal.Equals(work.TokenId, token.TokenId)))
            .OrderBy(token => token.AtElementId, StringComparer.Ordinal)
            .ToArray();

        foreach (var listenerToken in pendingListenerTokens)
        {
            var catcher = graph.EventSubprocessByElementId(listenerToken.AtElementId)
                ?? throw new BpmnExecutionException($"BPMN scope listener token '{listenerToken.TokenId}' resolved no event subprocess at '{listenerToken.AtElementId}'.");
            state = StartScopeListenerWorkFor(context, state, catcher, listenerToken.TokenId);
        }

        return state;
    }

    /// <summary>
    /// Arms a fresh scope listener in one shot: mints a <see cref="BpmnTokenKind.Listener"/>
    /// token and starts its listener work together. Used to re-arm after a non-interrupting fire, which always
    /// then starts the body, so the evaluation defers and is never terminal.
    /// </summary>
    private static BpmnExecutionState ArmScopeListener(
        BpmnEvaluationContext context,
        BpmnExecutionState state,
        BpmnEventSubprocessCatcher catcher,
        string producedBy)
    {
        var listenerToken = NewToken(state, catcher.ElementId, flowId: null, parentTokenId: null, BpmnTokenStatus.AwaitingChild,
            producingWorkHandle: producedBy, iterationKey: null, kind: BpmnTokenKind.Listener);
        state = AddToken(state, listenerToken);
        return StartScopeListenerWorkFor(context, state, catcher, listenerToken.TokenId);
    }

    /// <summary>Starts a listener token's suspending work and records the <c>ScopeListenerArmed</c> diagnostic.</summary>
    private static BpmnExecutionState StartScopeListenerWorkFor(
        BpmnEvaluationContext context,
        BpmnExecutionState state,
        BpmnEventSubprocessCatcher catcher,
        string listenerTokenId)
    {
        var listenerBindingRef = catcher.ListenerBindingRef
            ?? throw new BpmnExecutionException($"BPMN event subprocess '{catcher.ElementId}' binds no scope-listener work to arm; validation should have required it for a message, signal, or timer trigger.");

        state = StartWork(context, state, listenerBindingRef, catcher.ElementId, listenerTokenId, ScopeListenerSchedulingCause);
        return BpmnDiagnosticAccumulator.Add(state, BpmnDiagnosticKind.ScopeListenerArmed, catcher.ElementId, null, listenerTokenId,
            $"BPMN event subprocess '{catcher.ElementId}' armed a {catcher.TriggerKind} scope listener (work '{listenerBindingRef}').");
    }

    /// <summary>
    /// Reports a signal raised by a nested process this scope invoked — the receiving half of
    /// <see cref="BpmnHostCommand.SignalEnclosingScope"/>, and the way BPMN escalation crosses a scope boundary.
    /// <para>
    /// Resolves the signalling work to its host element, matches the payload's escalation code against the
    /// host's attached escalation boundary events and the scope's escalation event subprocesses (exact code
    /// beats the code-less catch-all; within a level the host boundary, being more local, beats the
    /// scope-level subprocess), and fires. A non-interrupting match mints a boundary token alongside the
    /// untouched host, so repeated signals fire repeatedly; an interrupting match tears the host down (a
    /// multi-instance coordinator cascading to its instances) before routing the boundary path. An unmatched
    /// escalation is re-signalled one hop further out when this process itself has an enclosing scope, and is
    /// a documented no-op at a root process. A signal that arrives after the signalling work terminalized
    /// fires non-interrupting boundaries but no-ops interrupting ones — interrupting retroactively would
    /// double-route the host's outbound. Any non-escalation code is a forward-compatible diagnostic
    /// pass-through. Never faults.
    /// </para>
    /// </summary>
    public BpmnEvaluation OnWorkSignalled(BpmnWorkSignalledRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var graph = request.Graph;
        var context = new BpmnEvaluationContext(request.Host);
        var state = request.PriorState ?? new BpmnExecutionState();

        // An unrecognized code (a future consumer's) is ignored with a diagnostic — never a fault.
        if (!StringComparer.Ordinal.Equals(request.Code, EscalationSignalCode))
        {
            state = BpmnDiagnosticAccumulator.Add(state, BpmnDiagnosticKind.EscalationUnhandled, null, null, null,
                $"BPMN process ignored a scope signal with unrecognized code '{request.Code}' (forward-compatible pass-through).");
            return FinishEvaluation(context, new EvaluationResult(state));
        }

        return FinishEvaluation(context, HandleEscalationSignal(context, graph, state, request));
    }

    /// <summary>Resolves, matches, and fires an escalation signal; the escalation-specific body of <see cref="OnWorkSignalled"/>.</summary>
    private EvaluationResult HandleEscalationSignal(
        BpmnEvaluationContext context,
        BpmnGraph graph,
        BpmnExecutionState state,
        BpmnWorkSignalledRequest signal)
    {
        var escalationCode = ReadPayloadCode(signal.Payload);

        // Resolve the host element from the signalling work's binding ref. This is robust to the late case: the
        // host element is graph-derived, so it resolves even after the signalling work stopped being live.
        var hostElement = graph.FindElementByBindingRef(signal.SignallingBindingRef);
        if (hostElement is null || escalationCode is null)
            return BubbleOrUnhandled(context, state, signal, escalationCode, hostElementId: hostElement?.ElementId);

        // The signalling work's record: present in the live case, absent in the late case where the work already
        // completed or failed and its record was dropped.
        var activeWork = state.ActiveWork.FirstOrDefault(work =>
            StringComparer.Ordinal.Equals(work.NodeId, signal.SignallingBindingRef) &&
            StringComparer.Ordinal.Equals(work.IterationId, signal.SignallingIterationId));
        var hostToken = activeWork is not null
            ? state.Tokens.FirstOrDefault(candidate => StringComparer.Ordinal.Equals(candidate.TokenId, activeWork.TokenId))
            : null;
        var isLate = hostToken is null || hostToken.Status is BpmnTokenStatus.Consumed or BpmnTokenStatus.Canceled;

        // The specificity ladder — (1) host boundary exact, (2) scope event subprocess exact, (3) host
        // boundary catch-all, (4) scope event subprocess catch-all, (5) bubble. Exact beats catch-all across kinds;
        // within a level the host boundary (more local to the throwing scope) beats the scope-level subprocess.
        var boundaries = graph.AttachedEscalationBoundaries(hostElement.ElementId);
        var boundaryExact = boundaries.FirstOrDefault(candidate => StringComparer.Ordinal.Equals(ReadEscalationBoundaryCode(candidate), escalationCode));
        var boundaryCatchAll = boundaries.FirstOrDefault(candidate => ReadEscalationBoundaryCode(candidate) is null);
        var eventSubprocessExact = graph.EscalationEventSubprocessExact(escalationCode);
        var eventSubprocessCatchAll = graph.EscalationCatchAllEventSubprocess();

        if (boundaryExact is not null)
            return FireEscalationBoundaryMatch(context, graph, state, boundaryExact, hostElement, hostToken, isLate, signal, escalationCode);
        if (eventSubprocessExact is not null)
            return FireEscalationEventSubprocessMatch(context, graph, state, eventSubprocessExact, hostToken, signal, escalationCode);
        if (boundaryCatchAll is not null)
            return FireEscalationBoundaryMatch(context, graph, state, boundaryCatchAll, hostElement, hostToken, isLate, signal, escalationCode);
        if (eventSubprocessCatchAll is not null)
            return FireEscalationEventSubprocessMatch(context, graph, state, eventSubprocessCatchAll, hostToken, signal, escalationCode);

        return BubbleOrUnhandled(context, state, signal, escalationCode, hostElement.ElementId);
    }

    /// <summary>
    /// Fires a matched escalation boundary event: a non-interrupting boundary fires, live or late, alongside the
    /// untouched host; an interrupting boundary cancels the host token (or its multi-instance coordinator) and
    /// routes, unless the host had already terminalized — a late race is a no-op with an <c>EscalationLate</c>
    /// diagnostic, because interrupting retroactively would double-route the host's outbound.
    /// </summary>
    private EvaluationResult FireEscalationBoundaryMatch(
        BpmnEvaluationContext context,
        BpmnGraph graph,
        BpmnExecutionState state,
        BpmnElement boundary,
        BpmnElement hostElement,
        BpmnToken? hostToken,
        bool isLate,
        BpmnWorkSignalledRequest signal,
        string escalationCode)
    {
        if (!boundary.CancelActivity)
            return FireEscalationBoundary(context, graph, state, boundary, hostToken, interruptTokenId: null, signal, escalationCode);

        if (isLate)
        {
            state = BpmnDiagnosticAccumulator.Add(state, BpmnDiagnosticKind.EscalationLate, boundary.ElementId, null, hostToken?.TokenId,
                $"BPMN interrupting escalation boundary '{boundary.ElementId}' matched code '{escalationCode}', but host '{hostElement.ElementId}' had already terminalized; no-op (late race).");
            return new EvaluationResult(state);
        }

        // The interrupt target is the multi-instance loop coordinator when the signalling work is an instance,
        // otherwise the host token itself.
        var interruptTokenId = ResolveMultiInstanceCoordinatorTokenId(state, hostToken!) ?? hostToken!.TokenId;
        return FireEscalationBoundary(context, graph, state, boundary, hostToken, interruptTokenId, signal, escalationCode);
    }

    /// <summary>
    /// Fires a matched scope escalation event subprocess on the signal path: activates the catcher — an
    /// interrupting one stops all other live work in the scope, including the host subprocess that escalated,
    /// with teardowns issued at the clean exit; a non-interrupting one leaves other scope work untouched —
    /// inheriting the escalation host token's iteration key, then propagates.
    /// </summary>
    private EvaluationResult FireEscalationEventSubprocessMatch(
        BpmnEvaluationContext context,
        BpmnGraph graph,
        BpmnExecutionState state,
        BpmnEventSubprocessCatcher catcher,
        BpmnToken? hostToken,
        BpmnWorkSignalledRequest signal,
        string escalationCode)
    {
        var pendingCancellations = new List<PendingTeardown>();
        var liveWorkHandles = catcher.Interrupting ? BuildLiveWorkHandles(context) : NoLiveWork;
        var activation = ActivateEventSubprocess(context, graph, state, catcher, hostToken?.IterationKey, liveWorkHandles,
            pendingCancellations, signal.SignallingHandle, $"escalation code '{escalationCode}'");

        var result = Propagate(context, graph, activation.State, context.Host.ScopeInstanceId);
        return result with { PendingTeardowns = pendingCancellations };
    }

    /// <summary>
    /// Fires a matched escalation boundary event. When <paramref name="interruptTokenId"/> is set (interrupting),
    /// cancels that token via the existing cascade — its work torn down, a multi-instance coordinator
    /// cascade-cancelling every instance — and its still-armed sibling catch listeners, before minting the
    /// boundary token; otherwise (non-interrupting) the host and its work are untouched. The minted
    /// <see cref="BpmnTokenStatus.Active"/> token inherits the host token's loop-iteration key and propagates the
    /// boundary's outbound flows. Teardowns are issued on the non-fault continuation.
    /// </summary>
    private EvaluationResult FireEscalationBoundary(
        BpmnEvaluationContext context,
        BpmnGraph graph,
        BpmnExecutionState state,
        BpmnElement boundary,
        BpmnToken? hostToken,
        string? interruptTokenId,
        BpmnWorkSignalledRequest signal,
        string escalationCode)
    {
        var pendingCancellations = new List<PendingTeardown>();

        if (interruptTokenId is not null)
        {
            var liveWorkHandles = BuildLiveWorkHandles(context);
            state = CancelTokenAndWork(state, interruptTokenId, EscalationHostInterruptedReason, liveWorkHandles, pendingCancellations,
                (token, reason) => $"BPMN interrupting escalation boundary '{boundary.ElementId}' matched code '{escalationCode}' and cancelled token '{token.TokenId}' at '{token.AtElementId}' ({reason}).");
            state = CancelHostListeners(graph, state, interruptTokenId, listenerTokenToSkip: null, EscalationHostInterruptedReason, liveWorkHandles, pendingCancellations);
        }

        var iterationKey = hostToken?.IterationKey;
        var boundaryToken = NewToken(state, boundary.ElementId, flowId: null, parentTokenId: hostToken?.TokenId, BpmnTokenStatus.Active,
            producingWorkHandle: signal.SignallingHandle, iterationKey: iterationKey);
        state = AddToken(state, boundaryToken);
        state = BpmnDiagnosticAccumulator.Add(state, BpmnDiagnosticKind.EscalationCaught, boundary.ElementId, null, boundaryToken.TokenId,
            $"BPMN {(interruptTokenId is null ? "non-interrupting" : "interrupting")} escalation boundary '{boundary.ElementId}' caught escalation code '{escalationCode}' and emitted token '{boundaryToken.TokenId}' to route the escalation path.");

        var producedBy = context.Host.ScopeInstanceId;
        var result = Propagate(context, graph, state, producedBy);
        return result with { PendingTeardowns = pendingCancellations };
    }

    /// <summary>
    /// Bubbles an unmatched escalation: when this process itself has an enclosing scope, carries a re-signal of
    /// the identical code and payload one hop further out (recursion on the receiving side, one hop per level);
    /// at a root process it is a no-op with an <c>EscalationUnhandled</c> diagnostic.
    /// </summary>
    private static EvaluationResult BubbleOrUnhandled(
        BpmnEvaluationContext context,
        BpmnExecutionState state,
        BpmnWorkSignalledRequest signal,
        string? escalationCode,
        string? hostElementId)
    {
        if (context.Host.HasEnclosingScope)
        {
            state = BpmnDiagnosticAccumulator.Add(state, BpmnDiagnosticKind.EscalationRaised, hostElementId, null, null,
                $"BPMN process found no escalation boundary for code '{escalationCode ?? "(none)"}'{(hostElementId is null ? "" : $" on host '{hostElementId}'")}; bubbling the escalation to its enclosing scope.");
            return new EvaluationResult(state, PendingScopeSignal: new PendingScopeSignal(signal.Code, signal.Payload));
        }

        state = BpmnDiagnosticAccumulator.Add(state, BpmnDiagnosticKind.EscalationUnhandled, hostElementId, null, null,
            $"BPMN root process found no escalation boundary for code '{escalationCode ?? "(none)"}'{(hostElementId is null ? "" : $" on host '{hostElementId}'")}; the escalation is unhandled (no-op).");
        return new EvaluationResult(state);
    }

    /// <summary>The escalation code and name an escalation throw or end event carries; the code is validated non-empty at graph build.</summary>
    private static (string Code, string? Name) ReadEscalation(BpmnElement element)
    {
        var properties = element.EventDefinitions.Single().Properties;
        var code = properties.TryGetValue(BpmnEventDefinitionProperties.Code, out var codeValue) && !string.IsNullOrWhiteSpace(codeValue)
            ? codeValue.Trim()
            : throw new BpmnExecutionException($"BPMN escalation event '{element.ElementId}' declares no escalation code; the graph validator should have rejected it.");
        var name = properties.TryGetValue(BpmnEventDefinitionProperties.Name, out var nameValue) && !string.IsNullOrWhiteSpace(nameValue)
            ? nameValue.Trim()
            : null;
        return (code, name);
    }

    /// <summary>The escalation code an escalation boundary declares, or <c>null</c> when it is the code-less catch-all.</summary>
    private static string? ReadEscalationBoundaryCode(BpmnElement boundary) =>
        boundary.EventDefinitions.SingleOrDefault() is { } definition
        && definition.Properties.TryGetValue(BpmnEventDefinitionProperties.Code, out var code)
        && !string.IsNullOrWhiteSpace(code)
            ? code.Trim()
            : null;

    /// <summary>Builds the escalation payload <c>{ code, name? }</c>; the escalation identity travels here, keeping the signal code namespace clean. <c>name</c> is omitted when absent.</summary>
    private static JsonElement BuildEscalationPayload(string code, string? name) =>
        JsonSerializer.SerializeToElement(new EscalationPayload(code, name), EscalationPayloadOptions);

    /// <summary>Reads the escalation code from an escalation payload; <c>null</c> when absent or malformed (defensive — the interpreter builds the payload, so unreachable in practice).</summary>
    private static string? ReadPayloadCode(JsonElement? payload) =>
        payload is { ValueKind: JsonValueKind.Object } element
        && element.TryGetProperty("code", out var code)
        && code.ValueKind == JsonValueKind.String
        && code.GetString() is { Length: > 0 } value
            ? value
            : null;

    private static readonly JsonSerializerOptions EscalationPayloadOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>The escalation payload shape: the required matching <c>code</c> and an optional display <c>name</c>.</summary>
    private sealed record EscalationPayload(string Code, string? Name = null);

    /// <summary>
    /// Handles a transaction that completed with the <c>Cancelled</c> outcome, in the enclosing
    /// scope. A cancelled transaction is not successfully-completed work: it consumes the host token, registers
    /// <b>no</b> compensable and routes <b>no</b> normal outbound flow, tears down the host's still-armed catch
    /// listeners (the host-completion teardown, reused), and mints an <see cref="BpmnTokenStatus.Active"/> token
    /// at the attached cancel boundary event, inheriting the host token's iteration key, so the boundary routes
    /// the cancellation path. When no cancel boundary is attached the enclosing scope faults deterministically
    /// (<see cref="TransactionCancelledUnhandledFaultCode"/>): graph validation cannot see into the nested scope
    /// to know a cancel end event exists there, so this is an execution-time rule.
    /// </summary>
    private EvaluationResult HandleTransactionCancelled(
        BpmnEvaluationContext context,
        BpmnGraph graph,
        BpmnExecutionState state,
        BpmnToken hostToken,
        IReadOnlyDictionary<(string BindingRef, string? IterationId), string> liveWorkHandles,
        List<PendingTeardown> pendingCancellations,
        string completedHandle)
    {
        var hostElement = graph.GetRequiredElement(hostToken.AtElementId);

        // Retire the host's still-armed catch listeners (the host-completion teardown, reused), then consume the
        // host token: no compensable is registered (the nested scope compensated itself) and nothing is routed.
        state = CancelHostListeners(graph, state, hostToken.TokenId, listenerTokenToSkip: null, BoundarySupersededByHostCompletionReason, liveWorkHandles, pendingCancellations);
        state = UpdateToken(state, hostToken with { Status = BpmnTokenStatus.Consumed });
        state = BpmnDiagnosticAccumulator.Add(state, BpmnDiagnosticKind.TransactionCancelled, hostElement.ElementId, null, hostToken.TokenId,
            $"BPMN transaction '{hostElement.ElementId}' completed with the '{CancelledOutcomeName}' outcome; routing the cancellation path.");

        var cancelBoundary = graph.AttachedCancelBoundary(hostElement.ElementId);
        if (cancelBoundary is null)
        {
            var message = $"BPMN transaction '{hostElement.ElementId}' completed with the '{CancelledOutcomeName}' outcome, but no cancel boundary event is attached to route the cancellation.";
            state = BpmnDiagnosticAccumulator.Add(state, BpmnDiagnosticKind.Faulted, hostElement.ElementId, null, hostToken.TokenId, message);
            return new EvaluationResult(state, new BpmnFault(TransactionCancelledUnhandledFaultCode, message));
        }

        // Mint an Active token at the cancel boundary, inheriting the host token's iteration key, and propagate:
        // the boundary's behavior routes its outbound flows unconditionally.
        var boundaryToken = NewToken(state, cancelBoundary.ElementId, flowId: null, parentTokenId: hostToken.TokenId, BpmnTokenStatus.Active, producingWorkHandle: completedHandle, iterationKey: hostToken.IterationKey);
        state = AddToken(state, boundaryToken);
        state = BpmnDiagnosticAccumulator.Add(state, BpmnDiagnosticKind.TokenEmitted, cancelBoundary.ElementId, null, boundaryToken.TokenId,
            $"BPMN cancel boundary '{cancelBoundary.ElementId}' fired and emitted token '{boundaryToken.TokenId}' to route the cancellation path.");

        var result = Propagate(context, graph, state, completedHandle);
        return result with { PendingTeardowns = pendingCancellations };
    }

    /// <summary>The multi-instance loop coordinator token id when <paramref name="token"/> is one of that loop's instance sub-tokens; <c>null</c> for an ordinary (non-instance) token.</summary>
    private static string? ResolveMultiInstanceCoordinatorTokenId(BpmnExecutionState state, BpmnToken token) =>
        token.ParentTokenId is { } parent
        && FindLoopByCoordinator(state, parent) is { } loop
        && StringComparer.Ordinal.Equals(token.AtElementId, loop.ElementId)
            ? parent
            : null;

    /// <summary>
    /// Starts a multi-instance loop: the arriving token becomes the loop coordinator (staying
    /// <c>AwaitingChild</c>), a <see cref="BpmnLoopState"/> record is written, the host's catch boundaries arm
    /// once (parented to the coordinator), and the bound work is started on private per-instance sub-tokens —
    /// one at a time in sequential mode, all up front in parallel mode. A loop of zero instances (reachable only
    /// through an empty collection) routes the host's outbound flows immediately.
    /// </summary>
    private EvaluationResult StartMultiInstanceLoop(
        BpmnEvaluationContext context,
        BpmnGraph graph,
        BpmnExecutionState state,
        BpmnElement element,
        BpmnToken coordinatorToken,
        string bindingRef,
        string producedBy)
    {
        var loopCharacteristics = element.LoopCharacteristics!;

        // Resolve the instance count N and, in collection mode, the per-instance item snapshot. Cardinality mode
        // takes N from the literal; collection mode reads the collection variable ONCE, a documented snapshot,
        // through the host's variable reader. A collection read failure faults deterministically.
        IReadOnlyList<JsonElement>? items = null;
        int total;
        if (loopCharacteristics.IsCollectionMode)
        {
            var resolution = ResolveCollectionInstances(context, state, element, loopCharacteristics);
            if (resolution.Fault is not null)
                return new EvaluationResult(resolution.State, resolution.Fault);
            state = resolution.State;
            items = resolution.Items;
            total = items?.Count ?? 0;
        }
        else
        {
            // A null cardinality here would be a graph the validator should already have rejected.
            total = loopCharacteristics.Cardinality
                ?? throw new BpmnExecutionException($"BPMN multi-instance element '{element.ElementId}' resolved no instance count; its loop characteristics declare neither a cardinality nor a collection.");
        }

        if (total <= 0)
            return RouteMultiInstanceCoordinatorOutbound(context, graph, state, element, coordinatorToken, producedBy);

        state = UpdateToken(state, coordinatorToken with { Status = BpmnTokenStatus.AwaitingChild });
        var (stateWithLoop, loop) = AddLoop(state, coordinatorToken.TokenId, element.ElementId, loopCharacteristics.IsSequential, total, items);
        state = stateWithLoop;
        state = BpmnDiagnosticAccumulator.Add(state, BpmnDiagnosticKind.Scheduled, element.ElementId, null, coordinatorToken.TokenId,
            $"BPMN multi-instance element '{element.ElementId}' started a {(loopCharacteristics.IsSequential ? "sequential" : "parallel")} loop of {total} instance(s).");

        // Arm the host's catch boundaries once, parented to the coordinator token.
        state = ArmCatchBoundaries(context, graph, state, element, coordinatorToken.TokenId, producedBy);

        var instanceCount = loopCharacteristics.IsSequential ? 1 : total;
        for (var index = 0; index < instanceCount; index++)
            state = StartMultiInstanceInstance(context, state, element, loop, bindingRef, index);

        state = UpdateLoop(state, loop with { NextIndex = instanceCount });
        return new EvaluationResult(state);
    }

    /// <summary>
    /// Reads a collection-mode host's collection variable once at loop start, returning the
    /// per-instance item snapshot, in array order, or a deterministic fault: the variable is unreadable (a
    /// defensive case, unreachable once the graph validated the name), holds a present non-array value, or is
    /// held outside the inline payload. A null or absent collection resolves to an empty snapshot, which the
    /// caller routes as an immediate zero-instance completion.
    /// </summary>
    private static (BpmnExecutionState State, IReadOnlyList<JsonElement>? Items, BpmnFault? Fault) ResolveCollectionInstances(
        BpmnEvaluationContext context,
        BpmnExecutionState state,
        BpmnElement element,
        BpmnLoopCharacteristics loopCharacteristics)
    {
        var variableName = loopCharacteristics.CollectionVariable!;
        if (!context.Host.Variables.TryRead(variableName, out var value))
        {
            var message = $"BPMN multi-instance element '{element.ElementId}' could not read collection variable '{variableName}'; it is not a visible declared container-scoped variable of the process.";
            state = BpmnDiagnosticAccumulator.Add(state, BpmnDiagnosticKind.Faulted, element.ElementId, null, null, message);
            return (state, null, new BpmnFault(CollectionUnreadableFaultCode, message));
        }

        // Null or absent resolves to an empty loop: complete immediately and route outbound.
        if (value.Presence is BpmnValuePresence.Absent or BpmnValuePresence.Null)
            return (state, [], null);

        // A present value the host holds outside the inline payload cannot be read from here.
        if (!value.HasValue || value.Json is not { } inline)
        {
            var message = $"BPMN multi-instance element '{element.ElementId}' collection variable '{variableName}' holds an externally-stored value; only an inline collection is executable in this slice.";
            state = BpmnDiagnosticAccumulator.Add(state, BpmnDiagnosticKind.Faulted, element.ElementId, null, null, message);
            return (state, null, new BpmnFault(CollectionNotInlineFaultCode, message));
        }

        if (inline.ValueKind != JsonValueKind.Array)
        {
            var message = $"BPMN multi-instance element '{element.ElementId}' collection variable '{variableName}' is a {inline.ValueKind} value, not a collection; a collection-mode loop requires an array.";
            state = BpmnDiagnosticAccumulator.Add(state, BpmnDiagnosticKind.Faulted, element.ElementId, null, null, message);
            return (state, null, new BpmnFault(CollectionNotACollectionFaultCode, message));
        }

        var items = inline.EnumerateArray().Select(item => item.Clone()).ToArray();
        return (state, items, null);
    }

    /// <summary>
    /// Starts one multi-instance instance: mints an <c>AwaitingChild</c> instance sub-token (parented to the
    /// coordinator, at the host element) and starts the bound work on it under an iteration scope seeding
    /// <c>loopIndex</c> and, in collection mode, the current item. The iteration id is minted deterministically
    /// from the Sequence-derived instance token id and is carried on both the start command and the active-work
    /// record, so several concurrent instances of the same binding resolve distinctly on completion.
    /// </summary>
    private static BpmnExecutionState StartMultiInstanceInstance(
        BpmnEvaluationContext context,
        BpmnExecutionState state,
        BpmnElement element,
        BpmnLoopState loop,
        string bindingRef,
        int index)
    {
        var scopeInstanceId = context.Host.ScopeInstanceId;
        // An instance sub-token inherits the coordinator token's loop-iteration key, so a multi-instance host
        // revisited across loop passes keeps each pass's instances in their own iteration.
        var coordinatorIterationKey = GetRequiredToken(state, loop.TokenId).IterationKey;
        var instanceToken = NewToken(state, element.ElementId, flowId: null, parentTokenId: loop.TokenId, BpmnTokenStatus.AwaitingChild, producingWorkHandle: scopeInstanceId, iterationKey: coordinatorIterationKey);
        state = AddToken(state, instanceToken);

        // In collection mode the instance's item comes from the loop-start snapshot on the loop record — the
        // variable is never re-read — and is seeded under the host's ItemVariable.
        var item = loop.Items is { } items && index < items.Count ? items[index] : (JsonElement?)null;
        var itemVariable = element.LoopCharacteristics!.IsCollectionMode ? element.LoopCharacteristics.ItemVariable : null;

        var iterationId = MultiInstanceIterationIdPrefix + instanceToken.TokenId;
        var iterationScope = BuildIterationScope(iterationId, index, item, itemVariable);
        state = StartWork(context, state, bindingRef, element.ElementId, instanceToken.TokenId, MultiInstanceSchedulingCause, iterationScope);

        return BpmnDiagnosticAccumulator.Add(state, BpmnDiagnosticKind.Scheduled, element.ElementId, null, instanceToken.TokenId,
            $"BPMN multi-instance element '{element.ElementId}' started instance {index} (work '{bindingRef}').");
    }

    /// <summary>
    /// Builds the per-instance iteration scope: the zero-based <c>loopIndex</c> and, in collection mode, the
    /// current item under the host element's <c>ItemVariable</c>, hinted <see cref="BpmnValueTypes.Any"/>.
    /// </summary>
    private static BpmnIterationScope BuildIterationScope(string iterationId, int index, JsonElement? item, string? itemVariable)
    {
        var values = new Dictionary<string, BpmnValue>(StringComparer.Ordinal)
        {
            [BpmnLoopCharacteristics.LoopIndexVariable] = BpmnValue.FromInteger(index)
        };
        if (itemVariable is not null && item is { } itemValue)
            values[itemVariable] = BpmnValue.From(itemValue, BpmnValueTypes.Any);

        return new BpmnIterationScope(iterationId, values);
    }

    /// <summary>
    /// Handles a multi-instance instance completion: consumes the instance sub-token and advances the loop.
    /// While instances remain, sequential mode starts the next instance and parallel mode simply records the
    /// completion. On the LAST instance the loop record is dropped, the coordinator's still-armed catch
    /// listeners are retired, and the coordinator routes the host element's outbound flows through its normal
    /// behavior — so a multi-instance host routes exactly once.
    /// </summary>
    private EvaluationResult HandleMultiInstanceInstanceCompletion(
        BpmnEvaluationContext context,
        BpmnGraph graph,
        BpmnExecutionState state,
        BpmnToken instanceToken,
        BpmnLoopState loop,
        IReadOnlyDictionary<(string BindingRef, string? IterationId), string> liveWorkHandles,
        List<PendingTeardown> pendingCancellations,
        string completedHandle)
    {
        state = UpdateToken(state, instanceToken with { Status = BpmnTokenStatus.Consumed });
        state = BpmnDiagnosticAccumulator.Add(state, BpmnDiagnosticKind.Consumed, loop.ElementId, null, instanceToken.TokenId,
            $"BPMN multi-instance element '{loop.ElementId}' instance token '{instanceToken.TokenId}' completed.");

        var completedCount = loop.CompletedCount + 1;
        if (completedCount < loop.TotalCount)
        {
            if (loop.IsSequential && loop.NextIndex < loop.TotalCount)
            {
                var hostElement = graph.GetRequiredElement(loop.ElementId);
                state = StartMultiInstanceInstance(context, state, hostElement, loop, hostElement.BindingRef!, loop.NextIndex);
                state = UpdateLoop(state, loop with { NextIndex = loop.NextIndex + 1, CompletedCount = completedCount });
            }
            else
            {
                state = UpdateLoop(state, loop with { CompletedCount = completedCount });
            }

            return new EvaluationResult(state, PendingTeardowns: pendingCancellations);
        }

        // Last instance: the loop is done — the coordinator "completes" and routes.
        state = RemoveLoop(state, loop.LoopId);
        var coordinator = GetRequiredToken(state, loop.TokenId);
        state = ApplyBoundaryCompletionSemantics(graph, state, coordinator, liveWorkHandles, pendingCancellations);

        var result = RouteMultiInstanceCoordinatorOutbound(context, graph, state, graph.GetRequiredElement(loop.ElementId), coordinator, completedHandle);
        return result with { PendingTeardowns = pendingCancellations };
    }

    /// <summary>Routes a multi-instance coordinator's outbound flows through the host element's normal behavior: task-flow selection with no outcome names, faulting <c>bpmn.flow.none-taken</c> when nothing matches, exactly as a single-run host.</summary>
    private EvaluationResult RouteMultiInstanceCoordinatorOutbound(
        BpmnEvaluationContext context,
        BpmnGraph graph,
        BpmnExecutionState state,
        BpmnElement element,
        BpmnToken coordinatorToken,
        string producedBy)
    {
        var behavior = _behaviors.GetRequired(BpmnElementFamilies.Resolve(element));
        var behaviorContext = new BpmnBehaviorContext(
            BpmnBehaviorTrigger.WorkCompleted,
            element,
            coordinatorToken,
            graph.OutboundFlows(element.ElementId),
            graph.InboundFlows(element.ElementId),
            [],
            state);

        var result = ApplyDecision(context, graph, state, coordinatorToken, element, Execute(behavior, behaviorContext, element), producedBy);
        if (result is { Fault: null, Terminated: false })
            result = Propagate(context, graph, result.State, producedBy);
        return result;
    }

    /// <summary>
    /// The result of one internal step. Faults, teardowns, and the outward signal are <b>carried</b> here and
    /// only turned into commands at the clean exit — see <see cref="FinishEvaluation"/>.
    /// <para>
    /// This is not the only teardown channel. A step inside the propagation loop returns a result the loop
    /// discards but for its state, so it carries teardowns on <see cref="BpmnEvaluationContext"/> instead; both
    /// drain in <see cref="IssueCarriedCommands"/> under the same non-fault rule.
    /// </para>
    /// </summary>
    private sealed record EvaluationResult(
        BpmnExecutionState State,
        BpmnFault? Fault = null,
        bool Terminated = false,
        IReadOnlyCollection<PendingTeardown>? PendingTeardowns = null,
        BpmnErrorDisposition? ErrorDisposition = null,
        PendingScopeSignal? PendingScopeSignal = null);

    /// <summary>A deterministic process failure, carried until the evaluation decides how to surface it.</summary>
    private sealed record BpmnFault(string Code, string Message);

    /// <summary>An escalation this scope could not match, to be re-signalled one hop outward. Issued only on a non-fault continuation.</summary>
    private sealed record PendingScopeSignal(string Code, JsonElement? Payload);

    /// <summary>Wraps a carried fault as a continuation.</summary>
    private static BpmnContinuation MakeFault(BpmnFault fault) => new BpmnContinuation.Fault(fault.Code, fault.Message);

    /// <summary>Packages the state, the continuation, and the commands produced so far as one evaluation.</summary>
    private static BpmnEvaluation Evaluated(BpmnEvaluationContext context, BpmnExecutionState state, BpmnContinuation continuation) =>
        new(state, continuation, context.Commands.ToArray());

    /// <summary>
    /// The token propagation loop: releases ready joins, then dispatches the first live
    /// <see cref="BpmnTokenStatus.Active"/> token to its element behavior, until the state is quiescent
    /// (every token consumed, parked at a join, or awaiting started work).
    /// </summary>
    private EvaluationResult Propagate(BpmnEvaluationContext context, BpmnGraph graph, BpmnExecutionState state, string producedBy)
    {
        while (true)
        {
            state = _tokenCoordinator.ReleaseReadyJoins(state, graph);

            var token = state.Tokens.FirstOrDefault(candidate => candidate.Status == BpmnTokenStatus.Active);
            if (token is null)
                return new EvaluationResult(state);

            var element = graph.GetRequiredElement(token.AtElementId);
            var behavior = _behaviors.GetRequired(BpmnElementFamilies.Resolve(element));
            var behaviorContext = new BpmnBehaviorContext(
                BpmnBehaviorTrigger.TokenArrived,
                element,
                token,
                graph.OutboundFlows(element.ElementId),
                graph.InboundFlows(element.ElementId),
                [],
                state);

            var result = ApplyDecision(context, graph, state, token, element, Execute(behavior, behaviorContext, element), producedBy);
            if (result.Fault is not null || result.Terminated)
                return result;

            state = result.State;
        }
    }

    private static BpmnBehaviorDecision Execute(IBpmnElementBehavior behavior, IBpmnBehaviorContext behaviorContext, BpmnElement element)
    {
        try
        {
            return behaviorContext.Trigger == BpmnBehaviorTrigger.TokenArrived
                ? behavior.OnTokenArrived(behaviorContext)
                : behavior.OnWorkCompleted(behaviorContext);
        }
        catch (BpmnExecutionException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new BpmnExecutionException($"BPMN behavior '{behavior.ElementFamily}' failed for element '{element.ElementId}'.", exception);
        }
    }

    /// <summary>
    /// Validates and applies one behavior decision. Mutation and work-dispatch authority stays here; behaviors
    /// only describe what should happen.
    /// </summary>
    private EvaluationResult ApplyDecision(
        BpmnEvaluationContext context,
        BpmnGraph graph,
        BpmnExecutionState state,
        BpmnToken token,
        BpmnElement element,
        BpmnBehaviorDecision decision,
        string producedBy)
    {
        // An own-scope escalation match is captured here and activated AFTER the whole throw decision has run —
        // its companion routing command first — so an interrupting activation's stop-others also stops the
        // throw's just-emitted successor.
        BpmnEventSubprocessCatcher? ownScopeActivation = null;

        foreach (var command in decision.Commands)
        {
            switch (command.Kind)
            {
                case BpmnBehaviorCommandKind.EmitTokens:
                {
                    state = UpdateToken(state, token with { Status = BpmnTokenStatus.Consumed });
                    if (command.FlowIds.Count == 0)
                    {
                        state = BpmnDiagnosticAccumulator.Add(state, BpmnDiagnosticKind.Consumed, element.ElementId, null, token.TokenId, $"BPMN token '{token.TokenId}' ended at element '{element.ElementId}' (no outbound sequence flow taken).");
                        break;
                    }

                    // The tokens an event-based gateway mints are the members of a first-catch-wins race.
                    var isEventBasedGateway = StringComparer.Ordinal.Equals(element.ElementType, BpmnElementTypes.EventBasedGateway);
                    var memberTokenIds = isEventBasedGateway ? new List<string>(command.FlowIds.Count) : null;

                    state = EmitFlowTokens(state, graph, element, token, command.FlowIds, producedBy, memberTokenIds);

                    if (memberTokenIds is { Count: > 0 })
                        state = AddRace(state, element.ElementId, memberTokenIds);

                    break;
                }
                case BpmnBehaviorCommandKind.TriggerCompensation:
                {
                    // The compensate throw or end token requests a compensation replay. TriggerCompensation is
                    // the sole command a compensate behavior emits, so the resolved result is returned directly.
                    return TriggerCompensation(context, graph, state, token, element, producedBy);
                }
                case BpmnBehaviorCommandKind.CancelTransaction:
                {
                    // The cancel end token requests the transaction cancellation. CancelTransaction is the sole
                    // command a cancel-end behavior emits, so the resolved result is returned directly.
                    return CancelTransaction(context, graph, state, token, element, producedBy);
                }
                case BpmnBehaviorCommandKind.RaiseEscalation:
                {
                    // The escalation throw or end event raises an escalation. RaiseEscalation is the FIRST
                    // command of the throw's decision (RaiseEscalation, then EmitTokens or ConsumeToken): it
                    // either records an own-scope catch, signals the enclosing scope, or records a root no-op
                    // diagnostic — never a fault — and the loop then processes the companion routing command.
                    var raised = RaiseEscalation(context, graph, state, token, element);
                    state = raised.State;
                    ownScopeActivation = raised.OwnScopeActivation;
                    break;
                }
                case BpmnBehaviorCommandKind.StartWork:
                {
                    if (element.BindingRef is not { } bindingRef)
                        throw new BpmnExecutionException($"BPMN behavior for element '{element.ElementId}' asked to start work, but the element binds none.");

                    graph.GetRequiredBoundWork(bindingRef);

                    // A multi-instance host does not start its bound work on the arriving token: the token
                    // becomes a loop coordinator and the interpreter runs the work N times on private
                    // per-instance sub-tokens. This is the only command a task or subprocess behavior emits, so
                    // the loop-start result is returned directly.
                    if (element.LoopCharacteristics is not null)
                        return StartMultiInstanceLoop(context, graph, state, element, token, bindingRef, producedBy);

                    state = UpdateToken(state, token with { Status = BpmnTokenStatus.AwaitingChild });
                    state = StartWork(context, state, bindingRef, element.ElementId, token.TokenId, $"element:{element.ElementType}");
                    state = BpmnDiagnosticAccumulator.Add(state, BpmnDiagnosticKind.Scheduled, element.ElementId, null, token.TokenId, $"BPMN element '{element.ElementId}' started work '{bindingRef}'.");
                    // Arm the host's catch boundaries alongside its bound work; error boundaries stay dormant.
                    state = ArmCatchBoundaries(context, graph, state, element, token.TokenId, producedBy);
                    break;
                }
                case BpmnBehaviorCommandKind.ConsumeToken:
                {
                    state = UpdateToken(state, token with { Status = BpmnTokenStatus.Consumed });
                    state = BpmnDiagnosticAccumulator.Add(state, BpmnDiagnosticKind.Consumed, element.ElementId, null, token.TokenId, $"BPMN end event '{element.ElementId}' consumed token '{token.TokenId}'.");
                    break;
                }
                case BpmnBehaviorCommandKind.TerminateProcess:
                {
                    state = UpdateToken(state, token with { Status = BpmnTokenStatus.Consumed });
                    // Work already running keeps running; its late completion is absorbed by the
                    // cancelled-token guard, or ignored outright once the process has completed.
                    state = CancelLiveWork(state);
                    state = state with { Terminated = true, Sequence = state.Sequence + 1 };
                    state = BpmnDiagnosticAccumulator.Add(state, BpmnDiagnosticKind.Terminated, element.ElementId, null, token.TokenId, command.Message ?? $"BPMN terminate end event '{element.ElementId}' ended the process.");
                    return new EvaluationResult(state, Terminated: true);
                }
                case BpmnBehaviorCommandKind.Fault:
                {
                    var faultCode = string.IsNullOrWhiteSpace(command.FaultCode) ? "bpmn.behavior.faulted" : command.FaultCode;
                    var message = command.Message ?? $"BPMN behavior for element '{element.ElementId}' faulted the process.";
                    state = BpmnDiagnosticAccumulator.Add(state, BpmnDiagnosticKind.Faulted, element.ElementId, null, token.TokenId, message);
                    return new EvaluationResult(state, new BpmnFault(faultCode, message));
                }
                default:
                    throw new BpmnExecutionException($"BPMN behavior command '{command.Kind}' is not supported.");
            }
        }

        // Activate the own-scope event subprocess now that the throw's routing command has run. This evaluation
        // rides the propagation loop, so an interrupting activation stops other live work logically only
        // (NoLiveWork); work already running is absorbed on late completion by the cancelled-token guard.
        if (ownScopeActivation is { } catcher)
        {
            var ownScopeCancellations = new List<PendingTeardown>();
            return ActivateEventSubprocess(context, graph, state, catcher, token.IterationKey, NoLiveWork, ownScopeCancellations,
                producedBy, $"own-scope escalation code '{ReadEscalation(element).Code}'");
        }

        return new EvaluationResult(state);
    }

    /// <summary>
    /// Mints one token per listed outbound flow from <paramref name="element"/>: a join target
    /// parks the token <c>WaitingAtJoin</c>, a backward (loop-back) edge mints a fresh iteration key, and every
    /// other edge inherits the emitting token's key. Shared by the <c>EmitTokens</c> command (which additionally
    /// collects the minted ids into <paramref name="memberTokenIds"/> for an event-based-gateway race) and by a
    /// compensate throw's outbound routing (<paramref name="memberTokenIds"/> null — a throw is never a
    /// gateway). The caller consumes the emitting token.
    /// </summary>
    private static BpmnExecutionState EmitFlowTokens(
        BpmnExecutionState state,
        BpmnGraph graph,
        BpmnElement element,
        BpmnToken token,
        IReadOnlyCollection<string> flowIds,
        string producedBy,
        List<string>? memberTokenIds)
    {
        foreach (var flowId in flowIds)
        {
            var flow = graph.GetRequiredFlow(flowId);
            if (!StringComparer.Ordinal.Equals(flow.SourceRef, element.ElementId))
                throw new BpmnExecutionException($"BPMN behavior for element '{element.ElementId}' emitted a token on flow '{flowId}', which does not originate from it.");

            var status = BpmnTokenCoordinator.ShouldWaitAtJoin(graph, flow.TargetRef) ? BpmnTokenStatus.WaitingAtJoin : BpmnTokenStatus.Active;
            var iterationKey = graph.IsBackwardFlow(flow.FlowId)
                ? NewIterationKey(state, flow.TargetRef)
                : token.IterationKey;
            var emitted = NewToken(state, flow.TargetRef, flow.FlowId, token.TokenId, status, producedBy, iterationKey);
            state = AddToken(state, emitted);
            memberTokenIds?.Add(emitted.TokenId);
            state = BpmnDiagnosticAccumulator.Add(
                state,
                status == BpmnTokenStatus.WaitingAtJoin ? BpmnDiagnosticKind.Waiting : BpmnDiagnosticKind.TokenEmitted,
                flow.TargetRef,
                flow.FlowId,
                emitted.TokenId,
                status == BpmnTokenStatus.WaitingAtJoin
                    ? $"BPMN token '{emitted.TokenId}' is waiting at join '{flow.TargetRef}'."
                    : $"BPMN token '{emitted.TokenId}' arrived at element '{flow.TargetRef}' via flow '{flow.FlowId}'.");
        }

        return state;
    }

    /// <summary>
    /// Handles a <c>TriggerCompensation</c> command: selects the target <c>Registered</c> compensables (all of
    /// them, or only the <c>activityRef</c> host's) in reverse registration order and claims them atomically in
    /// this evaluation. An empty selection completes the throw immediately — route or consume, no fault, no run.
    /// A non-empty selection parks the throw token as the run coordinator (<c>AwaitingChild</c>), writes the run
    /// record, and starts the first handler; the remaining handlers run one at a time, each driven by the
    /// previous handler's completion.
    /// </summary>
    private EvaluationResult TriggerCompensation(
        BpmnEvaluationContext context,
        BpmnGraph graph,
        BpmnExecutionState state,
        BpmnToken throwToken,
        BpmnElement throwElement,
        string producedBy)
    {
        var activityRef = ReadCompensationActivityRef(throwElement);

        // Reverse registration order = reverse completion order (newest-first). Compensables are appended in
        // registration order, so reversing the Registered subset yields the replay order; the claim flips are
        // committed within this one evaluation (a concurrent parallel-branch throw sees only still-Registered
        // records, so no compensable is ever claimed twice).
        var claimed = state.Compensables
            .Where(compensable => compensable.Status == BpmnCompensableStatus.Registered
                                  && (activityRef is null || StringComparer.Ordinal.Equals(compensable.HostElementId, activityRef)))
            .Reverse()
            .ToArray();

        if (claimed.Length == 0)
        {
            state = BpmnDiagnosticAccumulator.Add(state, BpmnDiagnosticKind.CompensationTriggered, throwElement.ElementId, null, throwToken.TokenId,
                $"BPMN compensate event '{throwElement.ElementId}' had nothing to compensate{(activityRef is null ? "" : $" for '{activityRef}'")}; completing immediately.");
            return CompleteThrow(context, graph, state, throwToken, throwElement, producedBy);
        }

        foreach (var compensable in claimed)
            state = UpdateCompensable(state, compensable with { Status = BpmnCompensableStatus.Claimed });

        state = UpdateToken(state, throwToken with { Status = BpmnTokenStatus.AwaitingChild });
        var (stateWithRun, run) = AddCompensationRun(state, throwToken.TokenId, claimed.Select(compensable => compensable.CompensableId).ToArray());
        state = stateWithRun;
        state = BpmnDiagnosticAccumulator.Add(state, BpmnDiagnosticKind.CompensationTriggered, throwElement.ElementId, null, throwToken.TokenId,
            $"BPMN compensate event '{throwElement.ElementId}' opened run '{run.RunId}' claiming {claimed.Length} compensable(s) (reverse registration order).");

        state = StartNextCompensationHandler(context, graph, state, run);
        return new EvaluationResult(state);
    }

    /// <summary>
    /// Handles a <c>CancelTransaction</c> command: a cancel end event fired inside a transaction
    /// scope. First it stops all OTHER live work — every live token except the cancel-end token is cancelled
    /// through <see cref="CancelTokenAndWork"/> so the multi-instance, race, and compensation-run coordinator
    /// cascades tear loops, races, and replays down consistently — and the work behind those tokens is torn
    /// down on the host, so an abandoned transaction leaves nothing running. It records the <c>Cancelling</c>
    /// verdict, then claims every <c>Registered</c> compensable (the whole scope) in reverse registration order and
    /// opens a <see cref="BpmnCompensationRun"/> coordinated by the cancel-end token, starting the first
    /// handler. An empty claim consumes the cancel-end token immediately; <see cref="FinishEvaluation"/> then
    /// completes the process with the <c>Cancelled</c> outcome once nothing is live.
    /// <para>
    /// Tearing the abandoned work down is what keeps the scope's slots honest. Stopping an in-flight
    /// compensation run releases its unrun compensables, and the fresh run re-claims them and restarts its head
    /// handler — the same element, under the same binding ref and inherited iteration key. Without the teardown
    /// the host would hold two live units for that one slot, which is the one thing
    /// <see cref="BpmnHostSnapshot"/> asks a host never to do and which it has no way to prevent by itself.
    /// </para>
    /// </summary>
    private EvaluationResult CancelTransaction(
        BpmnEvaluationContext context,
        BpmnGraph graph,
        BpmnExecutionState state,
        BpmnToken cancelEndToken,
        BpmnElement cancelEndElement,
        string producedBy)
    {
        // Step 1: stop all OTHER live work. The cancel-end token stays live as the coming replay's coordinator;
        // every other live token is cancelled, cascading loop, race, and compensation-run teardown, and the work
        // behind it is torn down on the host. The teardown is not optional here: step 2 re-claims the compensables
        // an in-flight run just released and restarts its head handler on the same (binding ref, iteration id)
        // slot, so leaving the old unit live would put two of them in one slot. The stop-others loop is shared
        // with an interrupting event-subprocess activation.
        var pendingCancellations = new List<PendingTeardown>();
        var (stoppedState, stoppedCount) = StopOtherLiveWork(state, cancelEndToken.TokenId, TransactionCancelledStopReason, BuildLiveWorkHandles(context), pendingCancellations,
            (token, reason) => $"BPMN cancel end event '{cancelEndElement.ElementId}' stopped live token '{token.TokenId}' at '{token.AtElementId}' ({reason}).");
        state = stoppedState;

        // A cancel end event is reached by a token arriving, so this runs inside the propagation loop, whose
        // result never reaches the caller intact. The teardowns ride the context to the clean exit instead.
        foreach (var teardown in pendingCancellations)
            context.CarryTeardown(teardown);

        state = state with { Cancelling = true, Sequence = state.Sequence + 1 };
        state = BpmnDiagnosticAccumulator.Add(state, BpmnDiagnosticKind.TransactionCancelled, cancelEndElement.ElementId, null, cancelEndToken.TokenId,
            $"BPMN cancel end event '{cancelEndElement.ElementId}' began cancelling the transaction (stopped {stoppedCount} other live token(s)).");

        // Step 2: claim every Registered compensable (the whole scope, reverse registration order) and open a
        // compensation run coordinated by the cancel-end token. An empty claim skips straight to completion.
        var claimed = state.Compensables
            .Where(compensable => compensable.Status == BpmnCompensableStatus.Registered)
            .Reverse()
            .ToArray();

        if (claimed.Length == 0)
        {
            state = UpdateToken(state, cancelEndToken with { Status = BpmnTokenStatus.Consumed });
            state = BpmnDiagnosticAccumulator.Add(state, BpmnDiagnosticKind.TransactionCancelled, cancelEndElement.ElementId, null, cancelEndToken.TokenId,
                $"BPMN cancel end event '{cancelEndElement.ElementId}' had nothing to compensate; completing the transaction with the '{CancelledOutcomeName}' outcome.");
            return new EvaluationResult(state);
        }

        foreach (var compensable in claimed)
            state = UpdateCompensable(state, compensable with { Status = BpmnCompensableStatus.Claimed });

        state = UpdateToken(state, cancelEndToken with { Status = BpmnTokenStatus.AwaitingChild });
        var (stateWithRun, run) = AddCompensationRun(state, cancelEndToken.TokenId, claimed.Select(compensable => compensable.CompensableId).ToArray());
        state = stateWithRun;
        state = BpmnDiagnosticAccumulator.Add(state, BpmnDiagnosticKind.CompensationTriggered, cancelEndElement.ElementId, null, cancelEndToken.TokenId,
            $"BPMN cancel end event '{cancelEndElement.ElementId}' opened run '{run.RunId}' claiming {claimed.Length} compensable(s) (reverse registration order).");

        state = StartNextCompensationHandler(context, graph, state, run);
        return new EvaluationResult(state);
    }

    /// <summary>
    /// Starts the head pending handler of a compensation run: mints an <c>AwaitingChild</c> sub-token at the
    /// handler element (parented to the coordinating throw token, inheriting its iteration key) and starts the
    /// handler element's bound work on it. One sub-token and one start per handler; the run replays sequentially
    /// in reverse registration order.
    /// </summary>
    private static BpmnExecutionState StartNextCompensationHandler(
        BpmnEvaluationContext context,
        BpmnGraph graph,
        BpmnExecutionState state,
        BpmnCompensationRun run)
    {
        var headCompensableId = run.PendingCompensableIds[0];
        var compensable = state.Compensables.First(candidate => StringComparer.Ordinal.Equals(candidate.CompensableId, headCompensableId));
        var handlerElement = graph.GetRequiredElement(compensable.HandlerElementId);
        var throwToken = GetRequiredToken(state, run.ThrowTokenId);
        var scopeInstanceId = context.Host.ScopeInstanceId;

        var handlerToken = NewToken(state, handlerElement.ElementId, flowId: null, parentTokenId: run.ThrowTokenId, BpmnTokenStatus.AwaitingChild,
            producingWorkHandle: scopeInstanceId, iterationKey: throwToken.IterationKey);
        state = AddToken(state, handlerToken);
        state = StartWork(context, state, handlerElement.BindingRef!, handlerElement.ElementId, handlerToken.TokenId, CompensationSchedulingCause);
        return BpmnDiagnosticAccumulator.Add(state, BpmnDiagnosticKind.Scheduled, handlerElement.ElementId, null, handlerToken.TokenId,
            $"BPMN compensation run '{run.RunId}' started handler '{handlerElement.ElementId}' (work '{handlerElement.BindingRef}') for compensable '{headCompensableId}'.");
    }

    /// <summary>
    /// Advances a compensation run when its head handler's sub-token completes. Consumes the sub-token, flips
    /// the head compensable to <c>Compensated</c>, then either starts the next pending handler or — when none
    /// remain — drops the run record and completes the coordinating throw (routing outbound for an intermediate
    /// throw, consuming for a compensate end). The handler completion was intercepted before behavior dispatch,
    /// so the sub-token never routes boundary or host semantics.
    /// </summary>
    private EvaluationResult HandleCompensationHandlerCompletion(
        BpmnEvaluationContext context,
        BpmnGraph graph,
        BpmnExecutionState state,
        BpmnToken handlerToken,
        BpmnCompensationRun run,
        string completedHandle)
    {
        state = UpdateToken(state, handlerToken with { Status = BpmnTokenStatus.Consumed });

        var headCompensableId = run.PendingCompensableIds[0];
        var compensable = state.Compensables.First(candidate => StringComparer.Ordinal.Equals(candidate.CompensableId, headCompensableId));
        state = UpdateCompensable(state, compensable with { Status = BpmnCompensableStatus.Compensated });
        state = BpmnDiagnosticAccumulator.Add(state, BpmnDiagnosticKind.Compensated, handlerToken.AtElementId, null, handlerToken.TokenId,
            $"BPMN compensation run '{run.RunId}' compensated '{headCompensableId}' (handler '{handlerToken.AtElementId}').");

        var remaining = run.PendingCompensableIds.Skip(1).ToArray();
        if (remaining.Length > 0)
        {
            state = UpdateCompensationRun(state, run with { PendingCompensableIds = remaining });
            var advanced = FindCompensationRun(state, run.ThrowTokenId)!;
            state = StartNextCompensationHandler(context, graph, state, advanced);
            return new EvaluationResult(state);
        }

        // Last handler: drop the run and complete the coordinating throw.
        state = RemoveCompensationRun(state, run.RunId);
        var throwToken = GetRequiredToken(state, run.ThrowTokenId);
        var throwElement = graph.GetRequiredElement(throwToken.AtElementId);

        // A cancel-end coordinator does NOT route or consume like a compensate throw or end event: it completes
        // the whole process with the Cancelled outcome. Consume the cancel-end token; FinishEvaluation then sees
        // Cancelling set with nothing live and completes with that outcome.
        if (BpmnElementFamilies.IsCancelEndEvent(throwElement))
        {
            state = UpdateToken(state, throwToken with { Status = BpmnTokenStatus.Consumed });
            state = BpmnDiagnosticAccumulator.Add(state, BpmnDiagnosticKind.TransactionCancelled, throwElement.ElementId, null, throwToken.TokenId,
                $"BPMN cancel end event '{throwElement.ElementId}' finished replaying the transaction's compensables; completing with the '{CancelledOutcomeName}' outcome.");
            return new EvaluationResult(state);
        }

        var result = CompleteThrow(context, graph, state, throwToken, throwElement, completedHandle);
        if (result is { Fault: null, Terminated: false })
            result = Propagate(context, graph, result.State, completedHandle);
        return result;
    }

    /// <summary>
    /// Completes a compensate throw or end token once its replay finished or found nothing to claim: a
    /// compensate end consumes its token (none-end semantics); a compensate intermediate throw routes its
    /// outbound flows through normal task-flow selection.
    /// </summary>
    private EvaluationResult CompleteThrow(
        BpmnEvaluationContext context,
        BpmnGraph graph,
        BpmnExecutionState state,
        BpmnToken throwToken,
        BpmnElement throwElement,
        string producedBy)
    {
        if (StringComparer.Ordinal.Equals(throwElement.ElementType, BpmnElementTypes.EndEvent))
        {
            state = UpdateToken(state, throwToken with { Status = BpmnTokenStatus.Consumed });
            state = BpmnDiagnosticAccumulator.Add(state, BpmnDiagnosticKind.Consumed, throwElement.ElementId, null, throwToken.TokenId,
                $"BPMN compensate end event '{throwElement.ElementId}' consumed token '{throwToken.TokenId}' after the compensation replay.");
            return new EvaluationResult(state);
        }

        var behaviorContext = new BpmnBehaviorContext(
            BpmnBehaviorTrigger.WorkCompleted, throwElement, throwToken,
            graph.OutboundFlows(throwElement.ElementId), graph.InboundFlows(throwElement.ElementId), [], state);
        var flows = BpmnFlowSelector.SelectTaskFlows(behaviorContext);
        if (flows.Count == 0 && behaviorContext.OutboundFlows.Count > 0)
        {
            var message = $"BPMN compensate throw event '{throwElement.ElementId}' completed its replay but no outbound sequence flow matched and no default flow is declared.";
            state = BpmnDiagnosticAccumulator.Add(state, BpmnDiagnosticKind.Faulted, throwElement.ElementId, null, throwToken.TokenId, message);
            return new EvaluationResult(state, new BpmnFault("bpmn.flow.none-taken", message));
        }

        state = UpdateToken(state, throwToken with { Status = BpmnTokenStatus.Consumed });
        state = EmitFlowTokens(state, graph, throwElement, throwToken, BpmnFlowSelector.FlowIds(flows), producedBy, memberTokenIds: null);
        return new EvaluationResult(state);
    }

    /// <summary>The <c>activityRef</c> a compensate throw/end targets, or <c>null</c> when it compensates every registration.</summary>
    private static string? ReadCompensationActivityRef(BpmnElement element) =>
        element.EventDefinitions.SingleOrDefault() is { } definition
        && definition.Properties.TryGetValue(BpmnEventDefinitionProperties.ActivityRef, out var value)
        && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;

    /// <summary>
    /// Picks the continuation and packages the evaluation.
    /// <para>
    /// A terminal continuation (Complete or Fault) may not be returned from an evaluation that also started
    /// work: the host would have to both discard the work and honor the completion. Terminate and behavior
    /// faults raised mid-propagation are therefore deferred — the state carries the verdict
    /// (<see cref="BpmnExecutionState.Terminated"/> or <see cref="BpmnExecutionState.PendingFault"/>) and the
    /// next work completion surfaces it.
    /// </para>
    /// <para>
    /// This is also the only place teardown and outward-signal commands are issued, and only on a non-fault
    /// continuation. Carrying them and issuing them last is what prevents premature teardown: work must not be
    /// stopped for a routing decision that the same evaluation then abandons by faulting.
    /// </para>
    /// </summary>
    private BpmnEvaluation FinishEvaluation(BpmnEvaluationContext context, EvaluationResult result)
    {
        var startedWorkThisEvaluation = context.StartedWork;
        var state = result.State;

        if (result.Fault is not null)
        {
            if (!startedWorkThisEvaluation)
                return Evaluated(context, state, MakeFault(result.Fault));

            state = CancelLiveWork(state);
            state = state with { PendingFault = new BpmnPendingFault(result.Fault.Code, result.Fault.Message), Sequence = state.Sequence + 1 };
            return Evaluated(context, state, BpmnContinuation.Defer.Instance);
        }

        if (state.PendingFault is { } pendingFault && !startedWorkThisEvaluation)
            return Evaluated(context, state, MakeFault(new BpmnFault(pendingFault.FaultCode, pendingFault.Message)));

        if (result.Terminated || state.Terminated)
        {
            return startedWorkThisEvaluation
                ? Evaluated(context, state, BpmnContinuation.Defer.Instance)
                : Evaluated(context, state, new BpmnContinuation.Complete(DoneOutcomeName));
        }

        var liveTokens = state.Tokens.Where(token => token.Status is BpmnTokenStatus.Active or BpmnTokenStatus.AwaitingChild or BpmnTokenStatus.WaitingAtJoin).ToArray();

        // Liveness is computed over NON-listener tokens and NON-listener work. A scope listener must never block
        // the scope from completing, and excluding a listener's work keeps the deadlock detector from misfiring on
        // a listener-plus-real-join state, where the excluded work would otherwise make the live-work count zero
        // while a genuine join-waiter sits there. The completion check and the deadlock detector use the SAME
        // filtered view. Teardown and stop sites stay unfiltered: listeners die with the scope.
        var listenerTokenIds = state.Tokens
            .Where(token => token.Kind == BpmnTokenKind.Listener)
            .Select(token => token.TokenId)
            .ToHashSet(StringComparer.Ordinal);
        var nonListenerLiveTokens = liveTokens.Where(token => !listenerTokenIds.Contains(token.TokenId)).ToArray();
        var nonListenerLiveWorkCount = state.ActiveWork.Count(child => !listenerTokenIds.Contains(child.TokenId));

        if (nonListenerLiveTokens.Length == 0 && nonListenerLiveWorkCount == 0)
        {
            // Only scope listeners (if any) remain live: the real work is done. Strictly AFTER the fault,
            // pending-fault, and terminate branches above (which own their teardown via CancelLiveWork), retire the
            // still-armed listeners — CancelTokenAndWork carries each live listener's teardown, folded into this
            // evaluation's commands in token-id ordinal order — then complete with the normal outcome selection.
            var liveListenerTokenIds = liveTokens
                .Where(token => listenerTokenIds.Contains(token.TokenId))
                .Select(token => token.TokenId)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();

            if (liveListenerTokenIds.Length > 0)
            {
                var pending = new List<PendingTeardown>(result.PendingTeardowns ?? []);
                var liveWorkHandles = BuildLiveWorkHandles(context);
                foreach (var listenerTokenId in liveListenerTokenIds)
                {
                    var listenerElementId = GetRequiredToken(state, listenerTokenId).AtElementId;
                    state = CancelTokenAndWork(state, listenerTokenId, EventSubprocessListenerSupersededByCompletionReason, liveWorkHandles, pending,
                        (token, reason) => $"BPMN scope listener token '{token.TokenId}' at '{token.AtElementId}' was retired because the scope completed ({reason}).");
                    state = BpmnDiagnosticAccumulator.Add(state, BpmnDiagnosticKind.ScopeListenerRetired, listenerElementId, null, listenerTokenId,
                        $"BPMN event subprocess '{listenerElementId}' scope listener '{listenerTokenId}' was retired when the scope completed.");
                }
                result = result with { State = state, PendingTeardowns = pending };
            }

            // Clean completion: the carried teardowns may now be issued.
            IssueCarriedCommands(context, result);
            // a cancelled transaction completes with the distinguishable Cancelled outcome (parent maps it
            // to the cancel boundary event) rather than Done.
            var outcomeName = state.Cancelling ? CancelledOutcomeName : DoneOutcomeName;
            state = BpmnDiagnosticAccumulator.Add(state, BpmnDiagnosticKind.Completed, null, null, null,
                $"BPMN process completed with the '{outcomeName}' outcome because no live tokens remain.");
            return Evaluated(context, state, new BpmnContinuation.Complete(outcomeName));
        }

        // A parked join arrival with nothing live left to produce the missing arrivals can never fire — a
        // parallel join downstream of an exclusive decision, say. Fault deterministically instead of leaving the
        // process running forever. Iteration-key join grouping does not weaken this: "all live tokens waiting at
        // a join, no live work" means nothing can produce a new arrival for ANY iteration group, so it is a true
        // deadlock however arrivals are grouped. The filtered view keeps a lone armed listener, which will never
        // produce a join arrival, from reading as a deadlock, while still catching a genuine join deadlock that
        // happens to sit alongside a live listener.
        if (nonListenerLiveWorkCount == 0 && nonListenerLiveTokens.Length > 0 && nonListenerLiveTokens.All(token => token.Status == BpmnTokenStatus.WaitingAtJoin))
        {
            var joinElementIds = string.Join(", ", nonListenerLiveTokens.Select(token => token.AtElementId).Distinct(StringComparer.Ordinal));
            var message = $"BPMN process deadlocked: token(s) wait at join(s) [{joinElementIds}] but no live token or running work can produce the missing arrival(s).";
            state = BpmnDiagnosticAccumulator.Add(state, BpmnDiagnosticKind.Faulted, null, null, null, message);
            return Evaluated(context, state, MakeFault(new BpmnFault("bpmn.join.deadlock", message)));
        }

        // Clean deferral: the carried teardowns may now be issued.
        IssueCarriedCommands(context, result);
        return Evaluated(context, state, BpmnContinuation.Defer.Instance);
    }

    /// <summary>
    /// Resolves a first-catch-wins race: flips every losing member token to <c>Canceled</c>, drops
    /// each loser's work record, marks the race resolved, and appends each loser whose armed work is a
    /// live unit of work to <paramref name="pendingCancellations"/> so it can be torn down on a non-fault
    /// continuation. A loser whose work is not live is a benign skip: the token is still cancelled, which absorbs
    /// its late completion. Purely logical — no command is issued here.
    /// </summary>
    private static BpmnExecutionState ResolveEventRace(
        BpmnExecutionState state,
        BpmnEventRace race,
        string winnerTokenId,
        IReadOnlyDictionary<(string BindingRef, string? IterationId), string> liveWorkHandles,
        List<PendingTeardown> pendingCancellations)
    {
        foreach (var loserTokenId in race.MemberTokenIds.Where(id => !StringComparer.Ordinal.Equals(id, winnerTokenId)))
            state = CancelTokenAndWork(state, loserTokenId, EventGatewayRaceCancellationReason, liveWorkHandles, pendingCancellations,
                (token, reason) => $"BPMN event-based gateway '{race.GatewayElementId}' cancelled losing race member token '{token.TokenId}' at '{token.AtElementId}' after the first catch won.");

        return MarkRaceResolved(state, race.RaceId);
    }

    /// <summary>
    /// Applies boundary-event completion semantics for the completing token, before the completing element's
    /// behavior is dispatched. A completing HOST token retires its still-armed catch listeners; an INTERRUPTING
    /// catch-listener completion tears down its host token, the host's bound work, and the host's sibling
    /// listeners; a NON-INTERRUPTING listener completion leaves everything else running. Teardowns are appended
    /// to <paramref name="pendingCancellations"/> for issuing on a non-fault continuation.
    /// </summary>
    private static BpmnExecutionState ApplyBoundaryCompletionSemantics(
        BpmnGraph graph,
        BpmnExecutionState state,
        BpmnToken completingToken,
        IReadOnlyDictionary<(string BindingRef, string? IterationId), string> liveWorkHandles,
        List<PendingTeardown> pendingCancellations)
    {
        var completingElement = graph.GetRequiredElement(completingToken.AtElementId);

        // Case A — the completing token is a boundary catch listener.
        if (StringComparer.Ordinal.Equals(completingElement.ElementType, BpmnElementTypes.BoundaryEvent))
        {
            if (!completingElement.CancelActivity || completingToken.ParentTokenId is not { } hostTokenId)
                return state; // non-interrupting (or a detached listener): host and siblings untouched.

            var hostToken = state.Tokens.FirstOrDefault(candidate => StringComparer.Ordinal.Equals(candidate.TokenId, hostTokenId));
            if (hostToken is null || hostToken.Status is BpmnTokenStatus.Consumed or BpmnTokenStatus.Canceled)
                return state; // the host already routed/was cancelled — nothing to interrupt.

            // Cancel the host token and its bound work, then the host's other listeners (skip the firing one).
            state = CancelTokenAndWork(state, hostTokenId, BoundaryHostInterruptedReason, liveWorkHandles, pendingCancellations,
                (token, reason) => $"BPMN interrupting boundary '{completingElement.ElementId}' cancelled host token '{token.TokenId}' at '{token.AtElementId}'.");
            return CancelHostListeners(graph, state, hostTokenId, listenerTokenToSkip: completingToken.TokenId, BoundaryHostInterruptedReason, liveWorkHandles, pendingCancellations);
        }

        // Case B — the completing token is a boundary host.
        // A successful host completion carrying an attached compensation boundary registers one compensable per
        // completion, so a host completed on several loop passes registers once per pass, each compensated
        // independently in reverse completion order. Only successful completions reach Case B; the fault and
        // cancel paths never do.
        if (graph.AttachedCompensationBoundary(completingElement.ElementId) is { CompensationHandlerElementId: { } handlerElementId })
        {
            var (registeredState, compensable) = AddCompensable(state, completingElement.ElementId, handlerElementId);
            state = BpmnDiagnosticAccumulator.Add(registeredState, BpmnDiagnosticKind.CompensationRegistered, completingElement.ElementId, null, completingToken.TokenId,
                $"BPMN host '{completingElement.ElementId}' completed and registered compensable '{compensable.CompensableId}' (handler '{handlerElementId}').");
        }

        // Retire the host's still-armed catch listeners.
        if (graph.AttachedCatchBoundaries(completingElement.ElementId).Count == 0)
            return state;

        return CancelHostListeners(graph, state, completingToken.TokenId, listenerTokenToSkip: null, BoundarySupersededByHostCompletionReason, liveWorkHandles, pendingCancellations);
    }

    /// <summary>Arms the host's catch boundaries: one <c>AwaitingChild</c> listener token per catch boundary, parented to the host token, each starting its listener work. Error boundaries arm nothing.</summary>
    private static BpmnExecutionState ArmCatchBoundaries(
        BpmnEvaluationContext context,
        BpmnGraph graph,
        BpmnExecutionState state,
        BpmnElement hostElement,
        string hostTokenId,
        string producedBy)
    {
        // Listeners inherit the host token's loop-iteration key, so a boundary host revisited across loop passes
        // arms a fresh listener per pass under the pass's iteration.
        var hostIterationKey = state.Tokens.FirstOrDefault(candidate => StringComparer.Ordinal.Equals(candidate.TokenId, hostTokenId))?.IterationKey;
        foreach (var boundary in graph.AttachedCatchBoundaries(hostElement.ElementId))
        {
            if (boundary.BindingRef is not { } listenerBindingRef)
                continue; // validation guarantees a catch boundary binds a listener; defensive.

            var listenerToken = NewToken(state, boundary.ElementId, flowId: null, parentTokenId: hostTokenId, BpmnTokenStatus.AwaitingChild, producingWorkHandle: producedBy, iterationKey: hostIterationKey);
            state = AddToken(state, listenerToken);
            state = StartWork(context, state, listenerBindingRef, boundary.ElementId, listenerToken.TokenId, "boundary:catch");
            state = BpmnDiagnosticAccumulator.Add(state, BpmnDiagnosticKind.Scheduled, boundary.ElementId, null, listenerToken.TokenId,
                $"BPMN boundary event '{boundary.ElementId}' armed listener work '{listenerBindingRef}' alongside host '{hostElement.ElementId}'.");
        }

        return state;
    }

    /// <summary>Cancels the still-armed catch listeners parented to <paramref name="hostTokenId"/> (optionally skipping the one that just fired), carrying each live listener's teardown.</summary>
    private static BpmnExecutionState CancelHostListeners(
        BpmnGraph graph,
        BpmnExecutionState state,
        string hostTokenId,
        string? listenerTokenToSkip,
        string reason,
        IReadOnlyDictionary<(string BindingRef, string? IterationId), string> liveWorkHandles,
        List<PendingTeardown> pendingCancellations)
    {
        var listenerTokenIds = state.Tokens
            .Where(candidate =>
                candidate.ParentTokenId is { } parent && StringComparer.Ordinal.Equals(parent, hostTokenId) &&
                (listenerTokenToSkip is null || !StringComparer.Ordinal.Equals(candidate.TokenId, listenerTokenToSkip)) &&
                graph.GetRequiredElement(candidate.AtElementId) is { } element && StringComparer.Ordinal.Equals(element.ElementType, BpmnElementTypes.BoundaryEvent))
            .Select(candidate => candidate.TokenId)
            .ToArray();

        foreach (var listenerTokenId in listenerTokenIds)
            state = CancelTokenAndWork(state, listenerTokenId, reason, liveWorkHandles, pendingCancellations,
                (token, cancellationReason) => $"BPMN boundary listener token '{token.TokenId}' at '{token.AtElementId}' was cancelled ({cancellationReason}).");

        return state;
    }

    /// <summary>
    /// Flips one live token to <c>Canceled</c>, drops its work record, and — when that work is still live —
    /// carries a teardown with <paramref name="reason"/>.
    /// A token that is already terminal, or whose work is not live, is a benign skip. When the token is a
    /// multi-instance loop coordinator, its live instance sub-tokens cascade-cancel first (their work resolved
    /// by binding ref and iteration id) and the loop record is dropped, so cancelling a multi-instance host
    /// cancels all of its live instances. Shared by the event-based-gateway race and the boundary teardown.
    /// </summary>
    private static BpmnExecutionState CancelTokenAndWork(
        BpmnExecutionState state,
        string tokenId,
        string reason,
        IReadOnlyDictionary<(string BindingRef, string? IterationId), string> liveWorkHandles,
        List<PendingTeardown> pendingCancellations,
        Func<BpmnToken, string, string> diagnosticMessage)
    {
        var token = state.Tokens.FirstOrDefault(candidate => StringComparer.Ordinal.Equals(candidate.TokenId, tokenId));
        if (token is null || token.Status is BpmnTokenStatus.Consumed or BpmnTokenStatus.Canceled)
            return state;

        // Cascade a multi-instance coordinator's cancellation to its live instance sub-tokens.
        if (FindLoopByCoordinator(state, tokenId) is { } loop)
        {
            foreach (var instanceTokenId in InstanceTokenIds(state, loop))
                state = CancelTokenAndWork(state, instanceTokenId, reason, liveWorkHandles, pendingCancellations, diagnosticMessage);
            state = RemoveLoop(state, loop.LoopId);
            token = GetRequiredToken(state, tokenId);
        }

        // Cascade a compensation run coordinator's cancellation to its live handler sub-token, then drop the
        // run and release its unrun Claimed compensables back to Registered: they never ran, so a later throw
        // may re-claim them. This mirrors the multi-instance coordinator cascade.
        if (FindCompensationRun(state, tokenId) is { } compensationRun)
        {
            foreach (var handlerTokenId in CompensationHandlerTokenIds(state, tokenId))
                state = CancelTokenAndWork(state, handlerTokenId, reason, liveWorkHandles, pendingCancellations, diagnosticMessage);

            foreach (var compensableId in compensationRun.PendingCompensableIds)
                if (state.Compensables.FirstOrDefault(candidate => StringComparer.Ordinal.Equals(candidate.CompensableId, compensableId)) is { Status: BpmnCompensableStatus.Claimed } claimed)
                    state = UpdateCompensable(state, claimed with { Status = BpmnCompensableStatus.Registered });

            state = RemoveCompensationRun(state, compensationRun.RunId);
            token = GetRequiredToken(state, tokenId);
        }

        var work = state.ActiveWork.FirstOrDefault(candidate => StringComparer.Ordinal.Equals(candidate.TokenId, tokenId));
        state = UpdateToken(state, token with { Status = BpmnTokenStatus.Canceled });
        if (work is not null)
        {
            state = RemoveActiveWork(state, tokenId);
            if (liveWorkHandles.TryGetValue((work.NodeId, work.IterationId), out var handle))
                pendingCancellations.Add(new PendingTeardown(handle, token.AtElementId, reason));
        }

        return BpmnDiagnosticAccumulator.Add(state, BpmnDiagnosticKind.Canceled, token.AtElementId, token.FlowId, token.TokenId, diagnosticMessage(token, reason));
    }

    /// <summary>
    /// Stops all live work in the scope except <paramref name="keepTokenId"/>: every other
    /// live token is cancelled through <see cref="CancelTokenAndWork"/> so the multi-instance, race, and
    /// compensation-run coordinator cascades tear loops, races, and replays down consistently. Shared by the
    /// cancel-transaction path (which keeps the cancel-end coordinator) and by an interrupting event-subprocess
    /// activation (which keeps the activation token). Whether the stopped work is torn down on the host or only
    /// dropped logically is the caller's call, made by what it passes as <paramref name="liveWorkHandles"/>.
    /// Returns the count of stopped tokens for the caller's diagnostic.
    /// </summary>
    private static (BpmnExecutionState State, int StoppedCount) StopOtherLiveWork(
        BpmnExecutionState state,
        string keepTokenId,
        string reason,
        IReadOnlyDictionary<(string BindingRef, string? IterationId), string> liveWorkHandles,
        List<PendingTeardown> pendingCancellations,
        Func<BpmnToken, string, string> diagnosticMessage)
    {
        var otherLiveTokenIds = state.Tokens
            .Where(candidate => candidate.Status is BpmnTokenStatus.Active or BpmnTokenStatus.AwaitingChild or BpmnTokenStatus.WaitingAtJoin
                                && !StringComparer.Ordinal.Equals(candidate.TokenId, keepTokenId))
            .Select(candidate => candidate.TokenId)
            .ToArray();
        foreach (var liveTokenId in otherLiveTokenIds)
            state = CancelTokenAndWork(state, liveTokenId, reason, liveWorkHandles, pendingCancellations, diagnosticMessage);
        return (state, otherLiveTokenIds.Length);
    }

    /// <summary>The instance sub-token ids of a live multi-instance loop: tokens parented to the coordinator and sitting at the host element.</summary>
    private static IReadOnlyCollection<string> InstanceTokenIds(BpmnExecutionState state, BpmnLoopState loop) =>
        state.Tokens
            .Where(candidate =>
                candidate.ParentTokenId is { } parent && StringComparer.Ordinal.Equals(parent, loop.TokenId) &&
                StringComparer.Ordinal.Equals(candidate.AtElementId, loop.ElementId))
            .Select(candidate => candidate.TokenId)
            .ToArray();

    /// <summary>The live handler sub-token ids of a compensation run: live tokens parented to the coordinating throw token (its handler sub-tokens sit at handler elements; ordinary emitted successors are consumed, so this resolves the in-flight handler).</summary>
    private static IReadOnlyCollection<string> CompensationHandlerTokenIds(BpmnExecutionState state, string throwTokenId) =>
        state.Tokens
            .Where(candidate =>
                candidate.ParentTokenId is { } parent && StringComparer.Ordinal.Equals(parent, throwTokenId) &&
                candidate.Status is BpmnTokenStatus.Active or BpmnTokenStatus.AwaitingChild or BpmnTokenStatus.WaitingAtJoin)
            .Select(candidate => candidate.TokenId)
            .ToArray();

    /// <summary>
    /// Asks the host to start the work bound to an element, and records it on the execution state. Both halves
    /// happen here and only here, so every path produces identical correlation and identical bookkeeping.
    /// </summary>
    private static BpmnExecutionState StartWork(
        BpmnEvaluationContext context,
        BpmnExecutionState state,
        string bindingRef,
        string elementId,
        string tokenId,
        string schedulingCause,
        BpmnIterationScope? iterationScope = null,
        string? startElementId = null)
    {
        var correlation = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [TokenIdCorrelationKey] = tokenId,
            [ElementIdCorrelationKey] = elementId,
            [SchedulingCauseCorrelationKey] = schedulingCause,
            [BindingRefCorrelationKey] = bindingRef
        };

        // An event subprocess body must begin at exactly one event-start element. The hint travels on the
        // correlation and is read back only under the event-subprocess-body scheduling cause, so an inherited
        // hint can never contaminate an ordinary nested process's seeding.
        if (!string.IsNullOrWhiteSpace(startElementId))
            correlation[StartElementIdCorrelationKey] = startElementId;

        context.AddStartWork(new BpmnHostCommand.StartWork(
            bindingRef, elementId, tokenId, schedulingCause, correlation, iterationScope, startElementId));

        return AddActiveWork(state, new BpmnActiveWork(bindingRef, elementId, tokenId, schedulingCause, iterationScope?.IterationId));
    }

    /// <summary>
    /// The host's live work keyed by <c>(binding ref, iteration id)</c>. Ordinary single-run work keys under a
    /// <c>null</c> iteration id; N concurrent instances of the same binding key under their distinct iteration
    /// ids, so a teardown resolves the right instance's handle.
    /// </summary>
    private static IReadOnlyDictionary<(string BindingRef, string? IterationId), string> BuildLiveWorkHandles(BpmnEvaluationContext context)
    {
        var liveWorkHandles = new Dictionary<(string BindingRef, string? IterationId), string>();
        foreach (var live in context.Host.LiveWork)
            liveWorkHandles[(live.BindingRef, live.IterationId)] = live.Handle;
        return liveWorkHandles;
    }

    /// <summary>
    /// Issues each carried teardown and the re-signalled escalation, if any. Only called from a non-fault
    /// continuation. Teardowns arrive on two channels — the result, for a step whose result reaches the caller,
    /// and the context, for one inside the propagation loop whose result does not — and both drain here, so the
    /// non-fault rule holds for either.
    /// </summary>
    private static void IssueCarriedCommands(BpmnEvaluationContext context, EvaluationResult result)
    {
        foreach (var teardown in (result.PendingTeardowns ?? []).Concat(context.CarriedTeardowns))
            context.AddCommand(new BpmnHostCommand.CancelWorkSubtree(teardown.Handle, teardown.ElementId, teardown.Reason));

        if (result.PendingScopeSignal is { } signal)
            context.AddCommand(new BpmnHostCommand.SignalEnclosingScope(signal.Code, signal.Payload));
    }

    /// <summary>Cancels every live token and drops the work records; work already running keeps running and its late completion is absorbed by the token-status guard.</summary>
    private static BpmnExecutionState CancelLiveWork(BpmnExecutionState state)
    {
        foreach (var liveToken in state.Tokens.Where(candidate => candidate.Status is BpmnTokenStatus.Active or BpmnTokenStatus.AwaitingChild or BpmnTokenStatus.WaitingAtJoin).ToArray())
            state = UpdateToken(state, liveToken with { Status = BpmnTokenStatus.Canceled });

        return state with { ActiveWork = [], Sequence = state.Sequence + 1 };
    }

    private static string ResolveTokenId(BpmnEvaluationContext context, BpmnExecutionState state, string completedBindingRef, string? completedIterationId)
    {
        var candidates = state.ActiveWork
            .Where(work => StringComparer.Ordinal.Equals(work.NodeId, completedBindingRef))
            .ToArray();

        // The primary path: the host hands back the correlation the interpreter issued with the start command, so
        // the token id it carries is authoritative — when the completing binding has NO live work (its work was
        // already torn down by a terminate or a cancel, the late-completion case absorbed by the cancelled-token
        // guard), or when the id names one of that binding's live work records. But when the binding HAS live work
        // and the correlated id names none of it, the correlation belongs to a nested process's own internal token
        // rather than to this scope's, so the by-binding resolution below owns it.
        if (context.Host.InvocationCorrelation.TryGetValue(TokenIdCorrelationKey, out var correlatedTokenId)
            && !string.IsNullOrWhiteSpace(correlatedTokenId)
            && (candidates.Length == 0 || candidates.Any(work => StringComparer.Ordinal.Equals(work.TokenId, correlatedTokenId))))
            return correlatedTokenId;

        // Resumed work may complete without the correlation. For N concurrent instances of the same binding the
        // completing iteration id disambiguates them; a single candidate resolves unambiguously.
        if (completedIterationId is not null &&
            candidates.FirstOrDefault(work => StringComparer.Ordinal.Equals(work.IterationId, completedIterationId)) is { } iterationMatch)
            return iterationMatch.TokenId;

        if (candidates.Length == 1)
            return candidates[0].TokenId;

        throw new BpmnExecutionException($"Unable to resolve the BPMN token for completed work '{completedBindingRef}' (iteration '{completedIterationId ?? "none"}').");
    }
}
