using MachineVoice.Protocol;

namespace MachineVoice.Core;

static class TtsRules
{
    public const int MaxModel = 256;
    public const int MaxVoice = 64;

    public static string? Validate(TtsSettingsDto settings)
    {
        if (!Enum.IsDefined(settings.Engine))
            return ProtocolErrors.InvalidArgument;
        if (!ValidPlaybackRate(settings.PlaybackRate))
            return ProtocolErrors.InvalidArgument;
        if (settings.SystemVoice is { } system && !(system.Rate is >= 0 and <= 1))
            return ProtocolErrors.InvalidArgument;
        if (settings.Qwen is not { } qwen)
            return null;
        if (!Uri.TryCreate(qwen.Endpoint?.Trim(), UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("http" or "https"))
            return ProtocolErrors.InvalidArgument;
        if (string.IsNullOrWhiteSpace(qwen.Model) || qwen.Model.Trim().Length > MaxModel)
            return ProtocolErrors.InvalidArgument;
        if (string.IsNullOrWhiteSpace(qwen.Voice) || qwen.Voice.Trim().Length > MaxVoice)
            return ProtocolErrors.InvalidArgument;
        if (qwen.UnloadAfterMinutes is < 0 or > QwenTtsSettingsDto.MaxUnloadAfterMinutes)
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
    };

    public static TtsSettingsDto WithPlaybackRate(TtsSettingsDto settings, double rate) => Canonical(new TtsSettingsDto
    {
        Engine = settings.Engine,
        PlaybackRate = rate,
        SystemVoice = settings.SystemVoice,
        Qwen = settings.Qwen,
    });

    /// <summary>Settings read from disk may be missing or broken; those fall back to the defaults.</summary>
    public static TtsSettingsDto Loaded(TtsSettingsDto? settings) =>
        settings is not null && Validate(settings) is null ? Canonical(settings) : new TtsSettingsDto();

    public static bool Same(QwenTtsSettingsDto a, QwenTtsSettingsDto b) =>
        a.Endpoint == b.Endpoint
        && a.Model == b.Model
        && a.Voice == b.Voice
        && a.UnloadAfterMinutes == b.UnloadAfterMinutes;
}
