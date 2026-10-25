using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Bpmn.Interchange;
using Bpmn.Model;
using Bpmn.Model.State;
using Bpmn.Runtime.InMemory;
using Bpmn.Semantics;

internal static class PackageConsumer
{
    private static readonly XNamespace Bpmn = "http://www.omg.org/spec/BPMN/20100524/MODEL";
    private static readonly XNamespace Acme = "urn:release-probe:metadata";
    private const string ForeignOneNamespace = "urn:release-probe:foreign-one";
    private const string ForeignTwoNamespace = "urn:release-probe:foreign-two";
    private const string SchemaNamespace = "http://www.w3.org/2001/XMLSchema";
    private const string LocalSchemaNamespace = "urn:release-probe:local-schema";

    private const string QNameDocument = """
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                          xmlns:acme="urn:release-probe:metadata"
                          xmlns:xsd="http://www.w3.org/2001/XMLSchema"
                          xmlns:foreign="urn:release-probe:foreign-one"
                          xmlns:v="urn:release-probe:foreign-one"
                          id="Definitions" targetNamespace="urn:release-probe:process"
                          acme:rootType="xsd:string" acme:foreignRootType="v:Thing">
          <bpmn:process id="Process" isExecutable="true">
            <bpmn:startEvent id="Start" />
            <bpmn:task id="Task" acme:inheritedType="xsd:string" acme:unboundType="ghost:Thing">
              <bpmn:extensionElements>
                <acme:vendor xmlns:xsd="urn:release-probe:local-schema" acme:localType="xsd:Thing" />
                <foreign:entry xmlns:v="urn:release-probe:foreign-two"
                               acme:childType="v:Other" acme:unboundType="p1:Missing" />
              </bpmn:extensionElements>
            </bpmn:task>
            <bpmn:endEvent id="End" />
            <bpmn:sequenceFlow id="ToTask" sourceRef="Start" targetRef="Task" />
            <bpmn:sequenceFlow id="ToEnd" sourceRef="Task" targetRef="End" />
          </bpmn:process>
        </bpmn:definitions>
        """;

    private const string HostDocument = """
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                          id="HostDefinitions" targetNamespace="urn:release-probe:host">
          <bpmn:process id="HostProcess" isExecutable="true">
            <bpmn:startEvent id="HostStart" />
            <bpmn:task id="HostTask" />
            <bpmn:endEvent id="HostEnd" />
            <bpmn:sequenceFlow id="HostToTask" sourceRef="HostStart" targetRef="HostTask" />
            <bpmn:sequenceFlow id="HostToEnd" sourceRef="HostTask" targetRef="HostEnd" />
          </bpmn:process>
        </bpmn:definitions>
        """;

    private static int Main()
    {
        try
        {
            CheckHostValueJson();
            CheckPositionalContracts();
            CheckWorkAvailability();
            CheckBindingDiscriminators();
            var qnameMetadata = CheckDefinitionBindingsAndXml();
            CheckReferenceHostJourney();

            var result = new
            {
                status = "passed",
                payloadFormatVersion = BpmnPayloadFormat.Version,
                qnameMetadata,
                targetFramework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                checks = new[]
                {
                    "legacy and refused BpmnValue JSON",
                    "positional constructor and deconstruction compatibility",
                    "producer command JSON and equality",
                    "structured work-availability assessment",
                    "timer and signal binding discriminators",
                    "definitions and bindings JSON/XML roundtrip",
                    "reference host completes a work item"
                }
            };
            Console.WriteLine("QUALIFICATION_RESULT_JSON=" + JsonSerializer.Serialize(result));
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void CheckHostValueJson()
    {
        var strict = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };

        var legacyFalse = JsonSerializer.Deserialize<BpmnValue>(
            """{"presence":2,"json":5,"HasValue":false}""", strict)!;
        Require(legacyFalse.HasValue, "Legacy HasValue=false overrode the value-derived getter.");

        var legacyTrue = JsonSerializer.Deserialize<BpmnValue>(
            """{"presence":0,"hasValue":true}""", strict)!;
        Require(!legacyTrue.HasValue, "Legacy hasValue=true overrode the value-derived getter.");

        var refused = BpmnValue.Refused("host.read-refused", "The host withheld this value.");
        var refusedJson = JsonSerializer.Serialize(refused);
        using var refusedDocument = JsonDocument.Parse(refusedJson);
        var refusedFields = refusedDocument.RootElement;
        Require(!refusedFields.TryGetProperty("HasValue", out _) && !refusedFields.TryGetProperty("hasValue", out _),
            "New BpmnValue JSON still writes the computed HasValue property.");
        Require(refusedFields.GetProperty("faultCode").GetString() == "host.read-refused",
            "Refused value JSON lost faultCode.");
        Require(refusedFields.GetProperty("faultMessage").GetString() == "The host withheld this value.",
            "Refused value JSON lost faultMessage.");

        var refusedAgain = JsonSerializer.Deserialize<BpmnValue>(refusedJson)!;
        Require(refusedAgain.Presence == BpmnValuePresence.Refused
                && refusedAgain.FaultCode == refused.FaultCode
                && refusedAgain.FaultMessage == refused.FaultMessage,
            "Refused value JSON did not round-trip.");

        Require((int)BpmnValuePresence.Absent == 0
                && (int)BpmnValuePresence.Null == 1
                && (int)BpmnValuePresence.Present == 2
                && (int)BpmnValuePresence.StoredExternally == 3
                && (int)BpmnValuePresence.Refused == 4,
            "BpmnValuePresence numeric ordinals changed.");

        var unknownFieldRejected = false;
        try
        {
            _ = JsonSerializer.Deserialize<BpmnValue>("""{"presence":0,"notAContractField":1}""", strict);
        }
        catch (JsonException)
        {
            unknownFieldRejected = true;
        }
        Require(unknownFieldRejected, "Strict legacy-value options did not reject a genuinely unknown field.");
    }

    private static void CheckPositionalContracts()
    {
        var value = new BpmnValue(BpmnValuePresence.Present, "integer", JsonSerializer.SerializeToElement(7));
        var (presence, typeHint, json) = value;
        Require(presence == BpmnValuePresence.Present && typeHint == "integer" && json?.GetInt32() == 7,
            "BpmnValue's three-position constructor/deconstruction contract changed.");

        var live = new BpmnLiveWork("binding", null, "handle") { WorkTokenId = "token" };
        var (liveBinding, liveIteration, liveHandle) = live;
        Require(liveBinding == "binding" && liveIteration is null && liveHandle == "handle"
                && live.WorkTokenId == "token",
            "BpmnLiveWork's positional contract or WorkTokenId init property changed.");

        var start = new BpmnHostCommand.StartWork(
            "binding", "Task", "token", "ordinary", new Dictionary<string, string>(), null, null);
        var (bindingRef, elementId, tokenId, schedulingCause, correlation, iterationScope, startHint) = start;
        Require(bindingRef == "binding" && elementId == "Task" && tokenId == "token"
                && schedulingCause == "ordinary" && correlation.Count == 0 && iterationScope is null && startHint is null,
            "StartWork's seven-position constructor/deconstruction contract changed.");

        var producer = start with { ProducedByHandle = "producer-handle" };
        Require(start != producer, "ProducedByHandle does not distinguish otherwise identical StartWork records.");
        Require(producer.ProducedByHandle == "producer-handle", "StartWork.ProducedByHandle did not retain its init value.");

        const string oldCommandJson = """{"BindingRef":"binding","ElementId":"Task","TokenId":"token","SchedulingCause":"ordinary","Correlation":{}}""";
        var oldCommand = JsonSerializer.Deserialize<BpmnHostCommand.StartWork>(oldCommandJson)!;
        Require(oldCommand.ProducedByHandle is null, "Old StartWork JSON without ProducedByHandle did not default to null.");
        using var commandJson = JsonDocument.Parse(JsonSerializer.Serialize(producer));
        Require(commandJson.RootElement.GetProperty("ProducedByHandle").GetString() == "producer-handle",
            "New StartWork JSON omitted producer provenance.");

        var completed = new BpmnWorkCompletedRequest(null!, null, null!, "binding", "handle", ["Done"], null)
        {
            WorkTokenId = "completed-token"
        };
        var (completedGraph, completedState, completedHost, completedBinding, completedHandle, outcomes, completedIteration) = completed;
        Require(completedGraph is null && completedState is null && completedHost is null
                && completedBinding == "binding" && completedHandle == "handle"
                && outcomes.Single() == "Done" && completedIteration is null
                && completed.WorkTokenId == "completed-token",
            "Completion callback's seven-position contract or WorkTokenId changed.");

        var faulted = new BpmnWorkFaultedRequest(null!, null, null!, "binding", "handle", "failure")
        {
            FaultedIterationId = "iteration",
            WorkTokenId = "fault-token",
            ErrorCode = "APP-1"
        };
        var (faultGraph, faultState, faultHost, faultBinding, faultHandle, faultMessage) = faulted;
        Require(faultGraph is null && faultState is null && faultHost is null
                && faultBinding == "binding" && faultHandle == "handle" && faultMessage == "failure"
                && faulted.FaultedIterationId == "iteration" && faulted.WorkTokenId == "fault-token"
                && faulted.ErrorCode == "APP-1",
            "Fault callback's six-position contract or init properties changed.");

        var signalled = new BpmnWorkSignalledRequest(null!, null, null!, "binding", "handle", "APP-1", null, "iteration")
        {
            WorkTokenId = "signal-token"
        };
        var (signalGraph, signalState, signalHost, signalBinding, signalHandle, code, payload, signalIteration) = signalled;
        Require(signalGraph is null && signalState is null && signalHost is null
                && signalBinding == "binding" && signalHandle == "handle" && code == "APP-1"
                && payload is null && signalIteration == "iteration" && signalled.WorkTokenId == "signal-token",
            "Signal callback's eight-position contract or WorkTokenId changed.");
    }

    private static void CheckWorkAvailability()
    {
        var bindings = new BpmnWorkBinding[]
        {
            new BpmnWorkBinding.UnboundTask("p", "unknown", "node-unknown", BpmnBindingSlot.Primary, "task"),
            new BpmnWorkBinding.UnboundTask("p", "available", "node-available", BpmnBindingSlot.Primary, "task"),
            new BpmnWorkBinding.UnboundTask("p", "unavailable", "node-unavailable", BpmnBindingSlot.Primary, "task")
        };

        var assessment = BpmnWorkAvailabilityValidator.Assess(bindings, binding => binding.ElementId switch
        {
            "available" => BpmnWorkAvailability.Available,
            "unavailable" => BpmnWorkAvailability.Unavailable,
            _ => BpmnWorkAvailability.Unknown
        });
        Require(assessment.Results.Select(result => result.Availability).SequenceEqual(
            [BpmnWorkAvailability.Unknown, BpmnWorkAvailability.Available, BpmnWorkAvailability.Unavailable]),
            "Work availability assessment lost a status or changed result order.");
        Require(!assessment.AllWorkAvailable, "Unknown/unavailable work was reported as ready.");

        var allAvailable = BpmnWorkAvailabilityValidator.Assess(bindings, _ => BpmnWorkAvailability.Available);
        Require(allAvailable.AllWorkAvailable, "An all-available assessment was not ready.");
    }

    private static void CheckBindingDiscriminators()
    {
        BpmnWorkBinding[] bindings =
        [
            new BpmnWorkBinding.SignalPublish("p", "signal", "node-signal", BpmnBindingSlot.Primary, "Order closed"),
            new BpmnWorkBinding.TimerDateWait("p", "date", "node-date", BpmnBindingSlot.Primary, "2026-12-24T18:00:00Z"),
            new BpmnWorkBinding.TimerCycleWait("p", "cycle", "node-cycle", BpmnBindingSlot.Primary,
                "R3/2026-01-01T00:00:00Z/PT1H", "PT1H", null, 3, "2026-01-01T00:00:00Z")
        ];

        var encoded = JsonSerializer.Serialize(bindings);
        using var document = JsonDocument.Parse(encoded);
        var kinds = document.RootElement.EnumerateArray().Select(item => item.GetProperty("kind").GetString()).ToArray();
        Require(kinds.SequenceEqual(["signalPublish", "timerDateWait", "timerCycleWait"]),
            "Published work-binding discriminators changed or are missing.");

        var decoded = JsonSerializer.Deserialize<BpmnWorkBinding[]>(encoded)!;
        Require(decoded[0] is BpmnWorkBinding.SignalPublish { SignalName: "Order closed" }
                && decoded[1] is BpmnWorkBinding.TimerDateWait { IsoDate: "2026-12-24T18:00:00Z" }
                && decoded[2] is BpmnWorkBinding.TimerCycleWait { Cycle: "R3/2026-01-01T00:00:00Z/PT1H", Repetitions: 3 },
            "Signal/timer work-binding cases did not round-trip through the packaged polymorphic contract.");
    }

    private static bool CheckDefinitionBindingsAndXml()
    {
        var reader = new BpmnXmlReader();
        var imported = reader.Read(QNameDocument);
        var json = JsonSerializer.Serialize(new DefinitionPayload(imported.Definitions, imported.Bindings));
        var received = JsonSerializer.Deserialize<DefinitionPayload>(json)
            ?? throw new InvalidOperationException("Definitions and bindings JSON did not deserialize.");

        Require(received.Definitions.FindProcess("Process") is not null,
            "Definitions were lost across the package JSON transfer.");
        Require(received.Bindings.Count == imported.Bindings.Count,
            "Work bindings were lost across the package JSON transfer.");

        var output = new BpmnXmlWriter().Write(received.Definitions, received.Bindings);
        var xml = XDocument.Parse(output);
        var process = xml.Root!.Element(Bpmn + "process")!;
        Require(process.Element(Bpmn + "task")?.Attribute("id")?.Value == "Task",
            "BPMN XML writer did not retain the task after the package JSON transfer.");

        var originalRootAttribute = imported.Definitions.Extensions.ForeignAttributes.Single(attribute => attribute.Name.LocalName == "rootType");
        var copiedRootAttribute = received.Definitions.Extensions.ForeignAttributes.Single(attribute => attribute.Name.LocalName == "rootType");
        Require(originalRootAttribute.Equals(copiedRootAttribute)
                && originalRootAttribute.GetHashCode() == copiedRootAttribute.GetHashCode(),
            "Retained foreign attributes do not compare structurally after JSON transfer.");

        var taskModel = imported.Definitions.FindProcess("Process")!.Elements.Single(element => element.ElementId == "Task");
        var copiedTaskModel = received.Definitions.FindProcess("Process")!.Elements.Single(element => element.ElementId == "Task");
        var originalLocalAttribute = taskModel.Extensions.ExtensionElements.Single().Attributes.Single(attribute => attribute.Name.LocalName == "localType");
        var copiedLocalAttribute = copiedTaskModel.Extensions.ExtensionElements.Single().Attributes.Single(attribute => attribute.Name.LocalName == "localType");
        Require(originalLocalAttribute.Equals(copiedLocalAttribute)
                && originalLocalAttribute.GetHashCode() == copiedLocalAttribute.GetHashCode(),
            "Nested retained attributes do not compare structurally after JSON transfer.");

        var metadataProperty = typeof(BpmnForeignAttribute).GetProperty("ValueNamespaces");
        Require(metadataProperty is not null, "The release omits the published ValueNamespaces API.");
        Require(typeof(BpmnForeignAttribute).Assembly.GetType("Bpmn.Model.BpmnNamespaceBinding") is not null,
            "ValueNamespaces exists without its published BpmnNamespaceBinding type.");

        var outputRoot = xml.Root!;
        AssertQNameValue(outputRoot, Acme + "rootType", "xsd:string", SchemaNamespace);
        AssertQNameValue(outputRoot, Acme + "foreignRootType", "v:Thing", ForeignOneNamespace);
        var outputTask = process.Element(Bpmn + "task")!;
        AssertQNameValue(outputTask, Acme + "inheritedType", "xsd:string", SchemaNamespace);
        AssertQNameValue(outputTask, Acme + "unboundType", "ghost:Thing", expectedNamespace: null);
        var extensions = outputTask.Element(Bpmn + "extensionElements")!;
        var vendor = extensions.Element(Acme + "vendor")!;
        AssertQNameValue(vendor, Acme + "localType", "xsd:Thing", LocalSchemaNamespace);
        var shadowedEntry = extensions.Element(XName.Get("entry", ForeignOneNamespace))!;
        Require(shadowedEntry.Name == XName.Get("entry", ForeignOneNamespace),
            "Foreign entry element name changed across package XML serialization.");
        AssertQNameValue(shadowedEntry, Acme + "childType", "v:Other", ForeignTwoNamespace);
        AssertQNameValue(shadowedEntry, Acme + "unboundType", "p1:Missing", expectedNamespace: null);
        return true;
    }

    private static void CheckReferenceHostJourney()
    {
        var definition = new BpmnXmlReader().Read(HostDocument).Definitions.Processes.Single();
        var host = new InMemoryBpmnHost();
        var instance = host.Start(definition);
        Require(instance.PendingWork.Count == 1, "Reference host did not start the package consumer's task.");
        instance.CompleteWork(instance.PendingWork.Single(), "Done");
        Require(instance.IsCompleted && instance.Outcome == BpmnInterpreter.DoneOutcomeName,
            "Reference host did not complete the package consumer's simple process.");
    }

    private static void AssertQNameValue(XElement element, XName attributeName, string expectedValue, string? expectedNamespace)
    {
        var value = (string?)element.Attribute(attributeName);
        Require(value == expectedValue, $"QName value {attributeName} changed from {expectedValue} to {value ?? "(missing)"}.");
        var prefix = expectedValue[..expectedValue.IndexOf(':')];
        var actual = element.GetNamespaceOfPrefix(prefix)?.NamespaceName;
        Require(actual == expectedNamespace,
            $"QName prefix {prefix} resolved to {actual ?? "(unbound)"}; expected {expectedNamespace ?? "(unbound)"}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed record DefinitionPayload(BpmnDefinitions Definitions, IReadOnlyList<BpmnWorkBinding> Bindings);
}
