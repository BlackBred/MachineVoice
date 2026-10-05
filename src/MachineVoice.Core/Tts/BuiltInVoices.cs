namespace MachineVoice.Core;

/// <summary>A voice that ships with MachineVoice and cannot be deleted.</summary>
public sealed record BuiltInVoice(string Id, string Name, string Text, Func<Stream> Open);

/// <summary>
/// The voices in the app: random OmniVoice voices picked by ear, and two Silero voices (baya, eugene) that OmniVoice
/// clones from their recordings. Each sample says <see cref="VoiceStudio.SampleText"/>.
/// </summary>
public static class BuiltInVoices
{
    public static IReadOnlyList<BuiltInVoice> All { get; } =
    [
        Embedded("alex", "Алекс"),
        Embedded("baya", "Байя"),
        Embedded("glen", "Глен"),
        Embedded("jack", "Джек"),
        Embedded("eugene", "Евгений"),
        Embedded("kate", "Кэйт"),
        Embedded("laura", "Лора"),
        Embedded("mike", "Майк"),
        Embedded("rob", "Роб"),
        Embedded("ros", "Рос"),
        Embedded("sara", "Сара"),
        Embedded("frank", "Фрэнк"),
    ];

    static BuiltInVoice Embedded(string id, string name) => new(id, name, VoiceStudio.SampleText, () =>
        typeof(BuiltInVoices).Assembly.GetManifestResourceStream($"MachineVoice.Core.Voices.{id}.wav")
        ?? throw new InvalidOperationException($"Missing embedded voice {id}."));
}
