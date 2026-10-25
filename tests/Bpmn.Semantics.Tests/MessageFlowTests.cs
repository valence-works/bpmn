using Bpmn.Model;
using Bpmn.Runtime.InMemory;
using Shouldly;
using Xunit;

namespace Bpmn.Semantics.Tests;

public sealed class MessageFlowTests
{
    [Theory]
    [InlineData(BpmnElementTypes.SendTask)]
    [InlineData(BpmnElementTypes.IntermediateThrowEvent)]
    [InlineData(BpmnElementTypes.EndEvent)]
    public void A_message_send_waits_for_its_bound_work_before_routing_or_ending(string elementType)
    {
        var instance = new InMemoryBpmnHost().Start(MessageProcess(elementType));

        instance.PendingWork.ShouldHaveSingleItem().BindingRef.ShouldBe("send");
        instance.IsFinished.ShouldBeFalse();

        instance.CompleteWork("send");

        if (elementType != BpmnElementTypes.EndEvent)
        {
            instance.PendingWork.ShouldHaveSingleItem().ElementId.ShouldBe("After");
            instance.IsFinished.ShouldBeFalse();
            instance.CompleteWork("after");
        }

        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
        instance.PendingWork.ShouldBeEmpty();
        instance.Transcript.SelectMany(entry => entry.Commands).OfType<BpmnHostCommand.StartWork>()
            .Count(command => command.BindingRef == "send").ShouldBe(1);
    }

    [Fact]
    public void A_receive_task_defers_the_next_task_until_its_work_completes()
    {
        var definition = new BpmnProcessBuilder("receive")
            .StartEvent("Start")
            .ReceiveTask("Receive", bindingRef: "receive")
            .Task("After", bindingRef: "after")
            .EndEvent("End")
            .ConnectSequence("Start", "Receive", "After", "End")
            .Build();
        var instance = new InMemoryBpmnHost().Start(definition);

        instance.PendingWork.ShouldHaveSingleItem().ElementId.ShouldBe("Receive");
        instance.IsFinished.ShouldBeFalse();

        instance.CompleteWork("receive");

        instance.PendingWork.ShouldHaveSingleItem().ElementId.ShouldBe("After");
        instance.CompleteWork("after");
        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
    }

    [Fact]
    public void A_sequential_multi_instance_send_dispatches_once_per_iteration()
    {
        var definition = new BpmnProcessBuilder("repeated-send")
            .StartEvent("Start")
            .Task(BpmnElementTypes.SendTask, "Send", bindingRef: "send",
                loopCharacteristics: new BpmnLoopCharacteristics(isSequential: true, cardinality: 2))
            .Task("After", bindingRef: "after")
            .EndEvent("End")
            .ConnectSequence("Start", "Send", "After", "End")
            .Build();
        var instance = new InMemoryBpmnHost().Start(definition);
        var handles = new HashSet<string>();

        for (var index = 0; index < 2; index++)
        {
            var send = instance.PendingWork.ShouldHaveSingleItem();
            send.ElementId.ShouldBe("Send");
            send.IterationValues["loopIndex"].Json!.Value.GetInt32().ShouldBe(index);
            handles.Add(send.Handle).ShouldBeTrue();
            instance.CompleteWork(send);
        }

        instance.PendingWork.ShouldHaveSingleItem().ElementId.ShouldBe("After");
        instance.CompleteWork("after");
        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
        instance.Transcript.SelectMany(entry => entry.Commands).OfType<BpmnHostCommand.StartWork>()
            .Count(command => command.BindingRef == "send").ShouldBe(2);
    }

    private static BpmnProcessDefinition MessageProcess(string elementType)
    {
        var builder = new BpmnProcessBuilder("message-send")
            .StartEvent("Start")
            .Element(new BpmnElement("Send", elementType, bindingRef: "send",
                eventDefinitions: elementType == BpmnElementTypes.SendTask ? [] : [ProcessFixtures.Message("sent")]))
            .Connect("Start", "Send");

        if (elementType != BpmnElementTypes.EndEvent)
            builder.Task("After", bindingRef: "after").EndEvent("End").ConnectSequence("Send", "After", "End");

        return builder.Build();
    }
}
