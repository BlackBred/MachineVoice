namespace MachineVoice.Core;

/// <summary>
/// Decides where a new item sits in the queue. Playback always takes the front.
/// </summary>
public interface IQueuePolicy
{
    void Enqueue(IList<SpeechItem> queue, SpeechItem item);
}
