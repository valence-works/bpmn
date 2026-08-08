namespace Bpmn.Model;

/// <summary>
/// Builds a <see cref="BpmnProcessDefinition"/> in code.
/// <para>
/// The model's constructors are perfectly usable directly, and for a handful of elements they are the
/// clearer choice. This exists for the case where they are not: assembling a process element by element
/// means threading two parallel collections and keeping the element ids in the flows consistent with the
/// element ids in the graph by hand, which is exactly the sort of thing a builder should do for you.
/// </para>
/// <para>
/// It validates as it goes. Adding two elements with the same id, or connecting an element that does not
/// exist, throws at the point of the mistake rather than producing a definition that fails later during
/// graph construction with less context.
/// </para>
/// </summary>
/// <example>
/// <code>
/// var process = new BpmnProcessBuilder("order")
/// .Name("Order handling")
/// .StartEvent("start")
/// .ServiceTask("charge", "Charge card")
/// .ExclusiveGateway("approved")
/// .UserTask("review", "Manual review")
/// .EndEvent("done")
/// .Connect("start", "charge")
/// .Connect("charge", "approved")
/// .Connect("approved", "review", condition: "rejected")
/// .Connect("approved", "done", isDefault: true)
/// .Connect("review", "done")
/// .Build();
/// </code>
/// </example>
public sealed class BpmnProcessBuilder
{
    private readonly string _processId;
    private readonly List<BpmnElement> _elements = [];
    private readonly List<BpmnSequenceFlow> _flows = [];
    private readonly List<BpmnLane> _lanes = [];
    private readonly List<BpmnVariableDeclaration> _variables = [];
    private readonly HashSet<string> _elementIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _flowIds = new(StringComparer.Ordinal);

    private string? _name;
    private bool _isExecutable = true;
    private bool _isTransaction;
    private BpmnExtensions _extensions = BpmnExtensions.Empty;
    private int _flowCounter;

    /// <summary>Starts building a process with the given BPMN id.</summary>
    public BpmnProcessBuilder(string processId)
    {
        if (string.IsNullOrWhiteSpace(processId))
            throw new ArgumentException("A process id is required.", nameof(processId));
        _processId = processId.Trim();
    }

    /// <summary>Sets the process name.</summary>
    public BpmnProcessBuilder Name(string name)
    {
        _name = name;
        return this;
    }

    /// <summary>Sets the BPMN <c>isExecutable</c> flag. Processes are executable by default.</summary>
    public BpmnProcessBuilder Executable(bool isExecutable = true)
    {
        _isExecutable = isExecutable;
        return this;
    }

    /// <summary>Marks the process a transaction, permitting a cancel end event within it.</summary>
    public BpmnProcessBuilder Transaction(bool isTransaction = true)
    {
        _isTransaction = isTransaction;
        return this;
    }

    /// <summary>Attaches retained foreign XML to the process element.</summary>
    public BpmnProcessBuilder Extensions(BpmnExtensions extensions)
    {
        _extensions = extensions;
        return this;
    }

    /// <summary>Declares a container-scoped variable.</summary>
    public BpmnProcessBuilder Variable(string name, string? typeHint = null)
    {
        _variables.Add(new BpmnVariableDeclaration(name, typeHint));
        return this;
    }

    /// <summary>Declares a lane.</summary>
    public BpmnProcessBuilder Lane(BpmnLane lane)
    {
        _lanes.Add(lane);
        return this;
    }

    // -- Events ------------------------------------------------------------------------------------

    /// <summary>Adds a start event.</summary>
    public BpmnProcessBuilder StartEvent(string elementId, string? name = null, params BpmnEventDefinition[] eventDefinitions) =>
    Element(new BpmnElement(elementId, BpmnElementTypes.StartEvent, name, eventDefinitions: eventDefinitions));

    /// <summary>Adds an end event.</summary>
    public BpmnProcessBuilder EndEvent(string elementId, string? name = null, params BpmnEventDefinition[] eventDefinitions) =>
    Element(new BpmnElement(elementId, BpmnElementTypes.EndEvent, name, eventDefinitions: eventDefinitions));

    /// <summary>Adds an intermediate catch event, which parks a token until its trigger fires.</summary>
    public BpmnProcessBuilder IntermediateCatchEvent(string elementId, BpmnEventDefinition eventDefinition, string? name = null, string? bindingRef = null) =>
    Element(new BpmnElement(elementId, BpmnElementTypes.IntermediateCatchEvent, name, bindingRef, eventDefinitions: [eventDefinition]));

    /// <summary>Adds an intermediate throw event, which raises its trigger and continues immediately.</summary>
    public BpmnProcessBuilder IntermediateThrowEvent(string elementId, BpmnEventDefinition eventDefinition, string? name = null, string? bindingRef = null) =>
    Element(new BpmnElement(elementId, BpmnElementTypes.IntermediateThrowEvent, name, bindingRef, eventDefinitions: [eventDefinition]));

    /// <summary>
    /// Attaches a boundary event to an existing activity. Interrupting by default, as BPMN specifies;
    /// error boundary events are always interrupting regardless of what is passed here.
    /// </summary>
    public BpmnProcessBuilder BoundaryEvent(
    string elementId,
    string attachedTo,
    BpmnEventDefinition eventDefinition,
    bool interrupting = true,
    string? name = null,
    string? bindingRef = null)
    {
        RequireElement(attachedTo, nameof(attachedTo));
        return Element(new BpmnElement(
        elementId,
        BpmnElementTypes.BoundaryEvent,
        name,
        bindingRef,
        attachedToRef: attachedTo,
        cancelActivity: interrupting,
        eventDefinitions: [eventDefinition]));
    }

    // -- Tasks -------------------------------------------------------------------------------------

    /// <summary>Adds an abstract task.</summary>
    public BpmnProcessBuilder Task(string elementId, string? name = null, string? bindingRef = null) =>
    Task(BpmnElementTypes.Task, elementId, name, bindingRef);

    /// <summary>Adds a user task.</summary>
    public BpmnProcessBuilder UserTask(string elementId, string? name = null, string? bindingRef = null) =>
    Task(BpmnElementTypes.UserTask, elementId, name, bindingRef);

    /// <summary>Adds a service task.</summary>
    public BpmnProcessBuilder ServiceTask(string elementId, string? name = null, string? bindingRef = null) =>
    Task(BpmnElementTypes.ServiceTask, elementId, name, bindingRef);

    /// <summary>Adds a script task. The host owns the script engine; this library never evaluates one.</summary>
    public BpmnProcessBuilder ScriptTask(string elementId, string? name = null, string? bindingRef = null) =>
    Task(BpmnElementTypes.ScriptTask, elementId, name, bindingRef);

    /// <summary>Adds a manual task.</summary>
    public BpmnProcessBuilder ManualTask(string elementId, string? name = null, string? bindingRef = null) =>
    Task(BpmnElementTypes.ManualTask, elementId, name, bindingRef);

    /// <summary>Adds a business rule task.</summary>
    public BpmnProcessBuilder BusinessRuleTask(string elementId, string? name = null, string? bindingRef = null) =>
    Task(BpmnElementTypes.BusinessRuleTask, elementId, name, bindingRef);

    /// <summary>Adds a send task, which publishes a message and continues.</summary>
    public BpmnProcessBuilder SendTask(string elementId, string? name = null, string? bindingRef = null) =>
    Task(BpmnElementTypes.SendTask, elementId, name, bindingRef);

    /// <summary>Adds a receive task, which waits for a named message.</summary>
    public BpmnProcessBuilder ReceiveTask(string elementId, string? name = null, string? bindingRef = null) =>
    Task(BpmnElementTypes.ReceiveTask, elementId, name, bindingRef);

    /// <summary>Adds a task of the given type with an optional multi-instance marker.</summary>
    public BpmnProcessBuilder Task(
    string elementType,
    string elementId,
    string? name = null,
    string? bindingRef = null,
    BpmnLoopCharacteristics? loopCharacteristics = null) =>
    Element(new BpmnElement(elementId, elementType, name, bindingRef ?? DefaultBindingRef(elementId), loopCharacteristics: loopCharacteristics));

    /// <summary>Adds a call activity, which invokes a separately defined process.</summary>
    public BpmnProcessBuilder CallActivity(string elementId, string calledElement, string? name = null, string? bindingRef = null)
    {
        var properties = new Dictionary<string, string>(StringComparer.Ordinal) { ["calledElement"] = calledElement };
        return Element(new BpmnElement(
        elementId,
        BpmnElementTypes.CallActivity,
        name,
        bindingRef ?? DefaultBindingRef(elementId),
        properties: properties));
    }

    /// <summary>Adds an embedded subprocess.</summary>
    public BpmnProcessBuilder SubProcess(
    string elementId,
    string? name = null,
    string? bindingRef = null,
    bool isTransaction = false,
    bool triggeredByEvent = false,
    BpmnLoopCharacteristics? loopCharacteristics = null) =>
    Element(new BpmnElement(
    elementId,
    BpmnElementTypes.SubProcess,
    name,
    bindingRef ?? DefaultBindingRef(elementId),
    loopCharacteristics: loopCharacteristics,
    isTransaction: isTransaction,
    triggeredByEvent: triggeredByEvent));

    // -- Gateways ----------------------------------------------------------------------------------

    /// <summary>Adds an exclusive (XOR) gateway. Routes a token down exactly one outgoing flow.</summary>
    public BpmnProcessBuilder ExclusiveGateway(string elementId, string? name = null, string? defaultFlowId = null) =>
    Element(new BpmnElement(elementId, BpmnElementTypes.ExclusiveGateway, name, defaultFlowId: defaultFlowId));

    /// <summary>Adds a parallel (AND) gateway. Splits into all outgoing flows and joins when all arrive.</summary>
    public BpmnProcessBuilder ParallelGateway(string elementId, string? name = null) =>
    Element(new BpmnElement(elementId, BpmnElementTypes.ParallelGateway, name));

    /// <summary>Adds an inclusive (OR) gateway.</summary>
    public BpmnProcessBuilder InclusiveGateway(string elementId, string? name = null, string? defaultFlowId = null) =>
    Element(new BpmnElement(elementId, BpmnElementTypes.InclusiveGateway, name, defaultFlowId: defaultFlowId));

    /// <summary>Adds an event-based gateway, which races its outgoing catch events.</summary>
    public BpmnProcessBuilder EventBasedGateway(string elementId, string? name = null) =>
    Element(new BpmnElement(elementId, BpmnElementTypes.EventBasedGateway, name));

    // -- Structure ---------------------------------------------------------------------------------

    /// <summary>Adds an already-constructed element, for cases the typed helpers do not cover.</summary>
    public BpmnProcessBuilder Element(BpmnElement element)
    {
        if (!_elementIds.Add(element.ElementId))
            throw new InvalidOperationException($"Element '{element.ElementId}' is already declared in process '{_processId}'.");

        _elements.Add(element);
        return this;
    }

    /// <summary>
    /// Connects two elements with a sequence flow. Both must already be declared, so a typo surfaces here
    /// rather than as a dangling reference during graph construction.
    /// </summary>
    /// <param name="source">The source element id.</param>
    /// <param name="target">The target element id.</param>
    /// <param name="condition">
    /// The outcome name that selects this flow at a gateway. Conditions are matched by name; this library
    /// evaluates no expressions.
    /// </param>
    /// <param name="isDefault">Whether this is the source gateway's default flow.</param>
    /// <param name="flowId">An explicit flow id. Generated when omitted.</param>
    /// <param name="name">An optional flow label.</param>
    public BpmnProcessBuilder Connect(
    string source,
    string target,
    string? condition = null,
    bool isDefault = false,
    string? flowId = null,
    string? name = null)
    {
        RequireElement(source, nameof(source));
        RequireElement(target, nameof(target));

        var id = flowId ?? NextFlowId(source, target);

        if (!_flowIds.Add(id))
            throw new InvalidOperationException($"Sequence flow '{id}' is already declared in process '{_processId}'.");

        _flows.Add(new BpmnSequenceFlow(id, source, target, name, condition, isDefault));

        if (isDefault)
        {
            var index = _elements.FindIndex(e => string.Equals(e.ElementId, source, StringComparison.Ordinal));
            var gateway = _elements[index];
            _elements[index] = new BpmnElement(
            gateway.ElementId,
            gateway.ElementType,
            gateway.Name,
            gateway.BindingRef,
            gateway.LaneId,
            defaultFlowId: id,
            eventDefinitions: gateway.EventDefinitions,
            properties: gateway.Properties,
            attachedToRef: gateway.AttachedToRef,
            cancelActivity: gateway.CancelActivity,
            loopCharacteristics: gateway.LoopCharacteristics,
            isForCompensation: gateway.IsForCompensation,
            compensationHandlerElementId: gateway.CompensationHandlerElementId,
            isTransaction: gateway.IsTransaction,
            triggeredByEvent: gateway.TriggeredByEvent,
            listenerBindingRef: gateway.ListenerBindingRef,
            extensions: gateway.Extensions);
        }

        return this;
    }

    /// <summary>
    /// Connects a run of elements in order, so a linear stretch reads as one call instead of several.
    /// </summary>
    public BpmnProcessBuilder ConnectSequence(params string[] elementIds)
    {
        if (elementIds.Length < 2)
            throw new ArgumentException("A sequence needs at least two elements.", nameof(elementIds));

        for (var i = 0; i < elementIds.Length - 1; i++)
            Connect(elementIds[i], elementIds[i + 1]);

        return this;
    }

    /// <summary>Produces the definition.</summary>
    public BpmnProcessDefinition Build() =>
    new(_processId, _name, _isExecutable, _isTransaction, _elements, _flows, _lanes, _variables, _extensions);

    private static string DefaultBindingRef(string elementId) => $"node-{elementId}";

    private string NextFlowId(string source, string target)
    {
        var candidate = $"flow-{source}-{target}";
        if (!_flowIds.Contains(candidate))
            return candidate;

        // Two elements can legitimately be connected more than once; fall back to a counter.
        do
        {
            _flowCounter++;
            candidate = $"flow-{source}-{target}-{_flowCounter}";
        } while (_flowIds.Contains(candidate));

        return candidate;
    }

    private void RequireElement(string elementId, string parameterName)
    {
        if (!_elementIds.Contains(elementId))
            throw new ArgumentException(
            $"Element '{elementId}' is not declared in process '{_processId}'. Declare it before connecting it.",
            parameterName);
    }
}

/// <summary>Builds a <see cref="BpmnDefinitions"/> document around one or more processes.</summary>
public sealed class BpmnDefinitionsBuilder
{
    private readonly List<BpmnProcessDefinition> _processes = [];
    private readonly List<BpmnMessageDeclaration> _messages = [];
    private readonly List<BpmnSignalDeclaration> _signals = [];
    private readonly List<BpmnErrorDeclaration> _errors = [];
    private readonly List<BpmnEscalationDeclaration> _escalations = [];

    private string? _id;
    private string? _targetNamespace;
    private string? _exporter;
    private string? _exporterVersion;
    private BpmnCollaboration? _collaboration;

    /// <summary>Sets the document id.</summary>
    public BpmnDefinitionsBuilder Id(string id)
    {
        _id = id;
        return this;
    }

    /// <summary>Sets the document's target namespace.</summary>
    public BpmnDefinitionsBuilder TargetNamespace(string targetNamespace)
    {
        _targetNamespace = targetNamespace;
        return this;
    }

    /// <summary>Records the tool that produced the document.</summary>
    public BpmnDefinitionsBuilder Exporter(string exporter, string? version = null)
    {
        _exporter = exporter;
        _exporterVersion = version;
        return this;
    }

    /// <summary>Adds a process.</summary>
    public BpmnDefinitionsBuilder Process(BpmnProcessDefinition process)
    {
        _processes.Add(process);
        return this;
    }

    /// <summary>Builds and adds a process inline.</summary>
    public BpmnDefinitionsBuilder Process(string processId, Action<BpmnProcessBuilder> configure)
    {
        var builder = new BpmnProcessBuilder(processId);
        configure(builder);
        return Process(builder.Build());
    }

    /// <summary>Sets the collaboration, for multi-pool documents.</summary>
    public BpmnDefinitionsBuilder Collaboration(BpmnCollaboration collaboration)
    {
        _collaboration = collaboration;
        return this;
    }

    /// <summary>Declares a message that event definitions can reference by id.</summary>
    public BpmnDefinitionsBuilder Message(string id, string? name = null)
    {
        _messages.Add(new BpmnMessageDeclaration(id, name));
        return this;
    }

    /// <summary>Declares a signal.</summary>
    public BpmnDefinitionsBuilder Signal(string id, string? name = null)
    {
        _signals.Add(new BpmnSignalDeclaration(id, name));
        return this;
    }

    /// <summary>Declares an error, carrying the code BPMN matches catching events on.</summary>
    public BpmnDefinitionsBuilder Error(string id, string? name = null, string? errorCode = null)
    {
        _errors.Add(new BpmnErrorDeclaration(id, name, errorCode));
        return this;
    }

    /// <summary>Declares an escalation.</summary>
    public BpmnDefinitionsBuilder Escalation(string id, string? name = null, string? escalationCode = null)
    {
        _escalations.Add(new BpmnEscalationDeclaration(id, name, escalationCode));
        return this;
    }

    /// <summary>Produces the document.</summary>
    public BpmnDefinitions Build() =>
    new(_id, _targetNamespace, _exporter, _exporterVersion, _processes, _collaboration,
    Diagrams: null, Messages: _messages, Signals: _signals, Errors: _errors, Escalations: _escalations);
}
