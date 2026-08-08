using System.Xml.Linq;

namespace Bpmn.Interchange;

/// <summary>
/// The canonical order BPMN's schema requires children to appear in.
/// <para>
/// BPMN's complex types are <c>xsd:sequence</c>s, not <c>xsd:all</c>s: <c>tFlowNode</c> wants
/// <c>documentation</c>, then <c>extensionElements</c>, then <c>incoming</c>, then <c>outgoing</c>, then its
/// type-specific content, and a document that puts them in any other order is invalid and gets rejected by
/// modeling tools. Retained content is therefore re-emitted into this order rather than appended.
/// </para>
/// <para>
/// The ranks are a single table across all owner types. Names are effectively disjoint between the types that
/// use them, and only the relative order within one owner has to be right, so one table is enough.
/// </para>
/// </summary>
internal static class BpmnChildOrder
{
    private const int Unknown = 90;

    private static readonly IReadOnlyDictionary<string, int> Ranks = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        // tBaseElement / tFlowElement
        ["documentation"] = 0,
        ["extensionElements"] = 1,
        ["auditing"] = 2,
        ["monitoring"] = 3,
        ["categoryValueRef"] = 4,

        // tFlowNode
        ["incoming"] = 5,
        ["outgoing"] = 6,

        // tActivity
        ["ioSpecification"] = 7,
        ["property"] = 8,
        ["dataInput"] = 9,
        ["dataOutput"] = 9,
        ["dataInputAssociation"] = 10,
        ["dataOutputAssociation"] = 11,
        ["inputSet"] = 12,
        ["outputSet"] = 12,
        ["resourceRole"] = 13,
        ["performer"] = 13,
        ["humanPerformer"] = 13,
        ["potentialOwner"] = 13,
        ["standardLoopCharacteristics"] = 14,
        ["multiInstanceLoopCharacteristics"] = 14,

        // tSequenceFlow, and root-level declarations under tDefinitions
        ["conditionExpression"] = 16,
        ["itemDefinition"] = 16,
        ["message"] = 16,
        ["signal"] = 16,
        ["error"] = 16,
        ["escalation"] = 16,
        ["category"] = 16,
        ["dataStore"] = 16,
        ["interface"] = 16,
        ["partnerEntity"] = 16,
        ["partnerRole"] = 16,
        ["resource"] = 16,
        ["import"] = 16,

        // Containers
        ["laneSet"] = 17,
        ["collaboration"] = 17,

        // Artifacts follow the flow elements they annotate
        ["association"] = 19,
        ["textAnnotation"] = 19,
        ["group"] = 19,

        ["relationship"] = 21
    };

    /// <summary>The position a child with this name takes among its siblings.</summary>
    public static int RankOf(XName name)
    {
        if (name == BpmnXmlNames.Di + "BPMNDiagram") return 20;
        if (name.Namespace != BpmnXmlNames.Model) return Unknown;

        var localName = name.LocalName;
        if (Ranks.TryGetValue(localName, out var rank)) return rank;

        // Every event definition sits with the other type-specific content of its event.
        if (localName.EndsWith("EventDefinition", StringComparison.Ordinal) || localName == "eventDefinitionRef") return 15;

        // Flow elements, including <process> under <definitions>.
        if (IsFlowElement(localName)) return 18;

        return Unknown;
    }

    private static bool IsFlowElement(string localName) =>
        localName is "process" or "startEvent" or "endEvent" or "intermediateCatchEvent" or "intermediateThrowEvent"
            or "boundaryEvent" or "subProcess" or "transaction" or "callActivity" or "adHocSubProcess"
            or "sequenceFlow" or "dataObject" or "dataObjectReference" or "dataStoreReference"
        || BpmnXmlNames.TaskLocalNamesToElementTypes.ContainsKey(localName)
        || BpmnXmlNames.GatewayLocalNamesToElementTypes.ContainsKey(localName);
}
