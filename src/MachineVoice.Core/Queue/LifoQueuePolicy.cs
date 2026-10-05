namespace MachineVoice.Core;

public sealed class LifoQueuePolicy : IQueuePolicy
{
    public void Enqueue(IList<SpeechItem> queue, SpeechItem item) => queue.Insert(0, item);
}
