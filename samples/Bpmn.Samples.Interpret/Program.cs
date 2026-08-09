using Bpmn.Model;
using Bpmn.Model.State;
using Bpmn.Runtime.InMemory;

// Walks a BPMN process one decision at a time, so you can see what the interpreter actually does.
//
// The point of this sample is what it *does not* do. When the interpreter decides a task should run, it
// emits "start the work bound to this element" and stops. Nothing here executes a task. The sample just
// asks you what the outcome was, hands that back, and prints the next decision.
//
// The timer makes the same point about time. The interpreter has no clock. It says "wait", and the host
// decides when that wait is over - here by advancing a virtual clock, so a three-day timer resolves
// instantly.
//
// Run with --non-interactive to have it choose the first outcome at every step (this is what CI does).

var interactive = !args.Contains("--non-interactive", StringComparer.Ordinal);

// --timer lets the deadline win instead of completing the manual step, which is the more interesting
// path: an interrupting boundary event tears down the activity it is attached to.
var preferTimer = args.Contains("--timer", StringComparer.Ordinal);

// A small approval process: a decision, a manual step with a deadline, and two ways to finish.
var process = new BpmnProcessBuilder("expense-approval")
    .Name("Expense approval")
    .StartEvent("submitted", "Expense submitted")
    .ServiceTask("classify", "Check the amount")
    .ServiceTask("auto-approve", "Auto-approve")
    .UserTask("review", "Manager review")
    .EndEvent("approved", "Approved")
    .EndEvent("rejected", "Rejected")
    .EndEvent("expired", "Expired")
    .BoundaryEvent(
        "review-deadline",
        attachedTo: "review",
        eventDefinition: new BpmnEventDefinition(BpmnEventDefinitionTypes.Timer),
        interrupting: true,
        name: "3 days",
        bindingRef: "node-review-deadline")
    .Connect("submitted", "classify")
    // The branch conditions hang off the element that REPORTS the outcome, not off a gateway. An
    // exclusive gateway routes on the outcome of its own bound work, and a gateway that binds none
    // always takes its default - so putting the conditions here is what makes the choice real.
    .Connect("classify", "review", condition: "over")
    .Connect("classify", "auto-approve", isDefault: true)
    .Connect("auto-approve", "approved")
    .Connect("review", "approved", condition: "approve")
    .Connect("review", "rejected", condition: "reject")
    .Connect("review-deadline", "expired")
    .Build();

var host = new InMemoryBpmnHost(new InMemoryBpmnHostOptions
{
    // The host learns durations from the bindings a document declares. Built in code, we say so here.
    TimerDurations = new Dictionary<string, TimeSpan>(StringComparer.Ordinal)
    {
        ["node-review-deadline"] = TimeSpan.FromDays(3)
    }
});

var instance = host.Start(process);

Console.WriteLine($"Started '{process.Name}'  (virtual clock at {instance.Clock.Now})");
Console.WriteLine();

var step = 0;

while (!instance.IsCompleted && step++ < 20)
{
    var pending = instance.PendingWork;

    if (pending.Count == 0)
    {
        Console.WriteLine("Nothing pending and not complete - the process cannot make progress.");
        break;
    }

    PrintState(instance);

    // A timer is the host's problem, not the interpreter's. Advancing the clock is how this host
    // decides the wait is over.
    var timer = pending.FirstOrDefault(w => w.DueAt is not null);
    if (timer is not null && (pending.Count == 1 || preferTimer))
    {
        Console.WriteLine($"  only a timer is pending, due at {timer.DueAt}");
        if (!Ask($"advance the clock to {timer.DueAt}?")) break;

        instance.Clock.AdvanceToNextTimer();
        Console.WriteLine($"  clock advanced to {instance.Clock.Now}; the timer fired");
        Console.WriteLine();
        continue;
    }

    var work = pending.First(w => w.DueAt is null);
    var outcomes = OutcomesFor(process, work.ElementId);

    var chosen = outcomes.Length switch
    {
        0 => null,
        1 => outcomes[0],
        _ => interactive ? Choose(work.ElementId, outcomes) : outcomes[0]
    };

    Console.WriteLine(chosen is null
        ? $"  completing '{work.ElementId}'"
        : $"  completing '{work.ElementId}' with outcome '{chosen}'");

    if (chosen is null) instance.CompleteWork(work);
    else instance.CompleteWork(work, chosen);

    Console.WriteLine();
}

Console.WriteLine(new string('=', 72));
Console.WriteLine(instance.IsCompleted
    ? $"Process completed with outcome '{instance.Outcome}' at virtual time {instance.Clock.Now}"
    : "Process is still running.");

Console.WriteLine();
Console.WriteLine("Transcript - every evaluation the interpreter performed:");
Console.WriteLine(instance.Transcript);

return instance.IsCompleted ? 0 : 1;

// ---------------------------------------------------------------------------------------------------

void PrintState(InMemoryProcessInstance current)
{
    var live = current.State.Tokens.Where(t => t.Status != BpmnTokenStatus.Consumed).ToArray();

    Console.WriteLine($"step {step}: {live.Length} live token(s)");
    foreach (var token in live.Take(6))
        Console.WriteLine($"  token at {token.AtElementId} ({token.Status})");

    foreach (var item in current.PendingWork)
        Console.WriteLine($"  pending: {item.ElementId}{(item.DueAt is { } due ? $" (timer due {due})" : "")}");
}

bool Ask(string question)
{
    if (!interactive) return true;
    Console.Write($"  {question} [Y/n] ");
    var answer = Console.ReadLine();
    return string.IsNullOrWhiteSpace(answer) || answer.Trim().StartsWith('y') || answer.Trim().StartsWith('Y');
}

string Choose(string elementId, string[] options)
{
    Console.WriteLine($"  '{elementId}' can report:");
    for (var i = 0; i < options.Length; i++) Console.WriteLine($"    [{i + 1}] {options[i]}");

    while (true)
    {
        Console.Write($"  choose 1-{options.Length}: ");
        var answer = Console.ReadLine();
        if (int.TryParse(answer, out var index) && index >= 1 && index <= options.Length)
            return options[index - 1];
        if (string.IsNullOrWhiteSpace(answer)) return options[0];
    }
}

// Which outcome names are worth offering for a piece of work: the conditions on its own outgoing
// flows. The interpreter matches an outcome name against those; it evaluates no expressions.
static string[] OutcomesFor(BpmnProcessDefinition definition, string elementId) =>
    ConditionsOn(definition, elementId);

static string[] ConditionsOn(BpmnProcessDefinition definition, string elementId) =>
    definition.SequenceFlows
        .Where(f => string.Equals(f.SourceRef, elementId, StringComparison.Ordinal))
        .Select(f => f.ConditionOutcome)
        .Where(c => !string.IsNullOrWhiteSpace(c))
        .Select(c => c!)
        .Distinct(StringComparer.Ordinal)
        .ToArray();

