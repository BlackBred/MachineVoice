namespace MachineVoice.Core;

public sealed class FifoQueuePolicy : IQueuePolicy
{
    public void Enqueue(IList<SpeechItem> queue, SpeechItem item) => queue.Add(item);
}
