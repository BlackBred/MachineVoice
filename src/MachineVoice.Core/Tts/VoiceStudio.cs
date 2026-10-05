using MachineVoice.Protocol;

namespace MachineVoice.Core;

/// <summary>Creates and keeps the voices that OmniVoice clones. Calls may come from any thread.</summary>
public interface IVoiceStudio
{
    IReadOnlyList<VoiceDto> List();

    /// <summary>A sample in a new random voice, kept as a draft until it is saved. Throws when the server fails.</summary>
    Task<VoiceDto> CreateDraftAsync(OmniVoiceTtsSettingsDto settings, CancellationToken cancellationToken);

    /// <summary>Null when there is no such draft.</summary>
    VoiceDto? Save(string draftId, string name);

    bool Delete(string voiceId);
}

public sealed class VoiceStudio(VoiceLibrary library, OmniVoiceSynthesizer synthesizer) : IVoiceStudio
{
    /// <summary>
    /// What a new voice says. The clip becomes the sample that every request clones, so it is short: a longer one
    /// slows down every sentence, and 5–10 seconds is enough for OmniVoice.
    /// </summary>
    public const string SampleText = "Привет! Я закончил задачу: проект собирается, тесты проходят, а изменения готовы к коммиту.";

    public IReadOnlyList<VoiceDto> List() => library.List();

    public async Task<VoiceDto> CreateDraftAsync(OmniVoiceTtsSettingsDto settings, CancellationToken cancellationToken)
    {
        var wav = await synthesizer.SampleAsync(settings, SampleText, cancellationToken).ConfigureAwait(false);
        return library.AddDraft(wav, SampleText);
    }

    public VoiceDto? Save(string draftId, string name) => library.Save(draftId, name);

    public bool Delete(string voiceId) => library.Delete(voiceId);
}
