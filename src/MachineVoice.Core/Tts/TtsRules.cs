using MachineVoice.Protocol;

namespace MachineVoice.Core;

static class TtsRules
{
    public const int MaxModel = 256;
    public const int MaxVoice = 64;
    public const int MaxLanguage = 16;

    public static string? Validate(TtsSettingsDto settings)
    {
        if (!Enum.IsDefined(settings.Engine))
            return ProtocolErrors.InvalidArgument;
        if (!ValidPlaybackRate(settings.PlaybackRate))
            return ProtocolErrors.InvalidArgument;
        if (settings.SystemVoice is { } system && !(system.Rate is >= 0 and <= 1))
            return ProtocolErrors.InvalidArgument;
        if (settings.Qwen is { } qwen
            && (!ValidModel(qwen) || string.IsNullOrWhiteSpace(qwen.Voice) || qwen.Voice.Trim().Length > MaxVoice))
            return ProtocolErrors.InvalidArgument;
        if (settings.OmniVoice is { } omni
            && (!ValidModel(omni) || (omni.Voice ?? "").Trim().Length > MaxVoice || !ValidLanguage(omni.Language)))
            return ProtocolErrors.InvalidArgument;
        return null;
    }

    public static bool ValidPlaybackRate(double rate) =>
        rate is >= TtsSettingsDto.MinPlaybackRate and <= TtsSettingsDto.MaxPlaybackRate;

    public static TtsSettingsDto Canonical(TtsSettingsDto settings) => new()
    {
        Engine = settings.Engine,
        PlaybackRate = Math.Round(settings.PlaybackRate, 2),
        SystemVoice = new SystemVoiceSettingsDto
        {
            Rate = Math.Round(settings.SystemVoice?.Rate ?? SystemVoiceSettingsDto.DefaultRate, 2),
        },
        Qwen = settings.Qwen is not { } qwen
            ? new QwenTtsSettingsDto()
            : new QwenTtsSettingsDto
            {
                Endpoint = qwen.Endpoint.Trim(),
                Model = qwen.Model.Trim(),
                Voice = qwen.Voice.Trim(),
                UnloadAfterMinutes = qwen.UnloadAfterMinutes,
            },
        OmniVoice = settings.OmniVoice is not { } omni
            ? new OmniVoiceTtsSettingsDto()
            : new OmniVoiceTtsSettingsDto
            {
                Endpoint = omni.Endpoint.Trim(),
                Model = omni.Model.Trim(),
                Voice = (omni.Voice ?? "").Trim(),
                Language = omni.Language.Trim().ToLowerInvariant(),
                UnloadAfterMinutes = omni.UnloadAfterMinutes,
            },
    };

    public static TtsSettingsDto WithPlaybackRate(TtsSettingsDto settings, double rate) => Canonical(new TtsSettingsDto
    {
        Engine = settings.Engine,
        PlaybackRate = rate,
        SystemVoice = settings.SystemVoice,
        Qwen = settings.Qwen,
        OmniVoice = settings.OmniVoice,
    });

    public static TtsSettingsDto WithOmniVoice(TtsSettingsDto settings, string voice) => Canonical(new TtsSettingsDto
    {
        Engine = settings.Engine,
        PlaybackRate = settings.PlaybackRate,
        SystemVoice = settings.SystemVoice,
        Qwen = settings.Qwen,
        OmniVoice = new OmniVoiceTtsSettingsDto
        {
            Endpoint = settings.OmniVoice.Endpoint,
            Model = settings.OmniVoice.Model,
            Voice = voice,
            Language = settings.OmniVoice.Language,
            UnloadAfterMinutes = settings.OmniVoice.UnloadAfterMinutes,
        },
    });

    /// <summary>Settings read from disk may be missing or broken; those fall back to the defaults.</summary>
    public static TtsSettingsDto Loaded(TtsSettingsDto? settings) =>
        settings is not null && Validate(settings) is null ? Canonical(settings) : new TtsSettingsDto();

    public static bool Same(QwenTtsSettingsDto a, QwenTtsSettingsDto b) =>
        a.Endpoint == b.Endpoint
        && a.Model == b.Model
        && a.Voice == b.Voice
        && a.UnloadAfterMinutes == b.UnloadAfterMinutes;

    public static bool Same(OmniVoiceTtsSettingsDto a, OmniVoiceTtsSettingsDto b) =>
        a.Endpoint == b.Endpoint
        && a.Model == b.Model
        && a.Voice == b.Voice
        && a.Language == b.Language
        && a.UnloadAfterMinutes == b.UnloadAfterMinutes;

    static bool ValidModel(IMlxModelSettings settings) =>
        Uri.TryCreate(settings.Endpoint?.Trim(), UriKind.Absolute, out var endpoint)
        && endpoint.Scheme is "http" or "https"
        && !string.IsNullOrWhiteSpace(settings.Model)
        && settings.Model.Trim().Length <= MaxModel
        && settings.UnloadAfterMinutes is >= 0 and <= QwenTtsSettingsDto.MaxUnloadAfterMinutes;

    static bool ValidLanguage(string? language) =>
        language?.Trim() is { Length: > 0 and <= MaxLanguage } tag && tag.All(c => char.IsAsciiLetter(c) || c == '-');
}
