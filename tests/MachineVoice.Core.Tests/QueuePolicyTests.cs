using MachineVoice.Core;

namespace MachineVoice.Core.Tests;

public class QueuePolicyTests
{
    [Fact]
    public void Fifo_Appends()
    {
        var queue = new List<SpeechItem>();
        var policy = new FifoQueuePolicy();
        policy.Enqueue(queue, Item("a"));
        policy.Enqueue(queue, Item("b"));
        policy.Enqueue(queue, Item("c"));

        Assert.Equal(["a", "b", "c"], queue.Select(item => item.GenerationId).ToArray());
    }

    static SpeechItem Item(string generationId) => new()
    {
        Id = generationId,
        Source = "cursor",
        GenerationId = generationId,
        Text = generationId,
        ReceivedAt = DateTimeOffset.UnixEpoch,
    };
}
