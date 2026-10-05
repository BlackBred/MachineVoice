using MachineVoice.Protocol;

namespace MachineVoice.App;

/// <summary>What all engines share: which one reads, which one takes over, and the playback rate.</summary>
public partial class VoicePage : SettingsPage
{
    readonly Shown<Values> _shown = new(new Values(TtsEngineKind.System, TtsSettingsDto.DefaultPlaybackRate));

    public VoicePage()
    {
        InitializeComponent();
        Watch(EngineSystem, EngineQwen, EngineOmni, PlaybackRate);
        UseSaveBar(SaveButton, SaveStatus);
        Show(_shown.Value);
    }

    public override string Title => "Голос";

    public override bool Edited => Read() != _shown.Value;

    protected override string SavedMessage => _shown.Value.Engine is TtsEngineKind.Qwen or TtsEngineKind.OmniVoice
        ? "Сохранено. Модель загружается в фоне, первая фраза может подождать."
        : "Сохранено";

    public override void Update(ControlState state)
    {
        var saved = new Values(state.Settings.Tts.Engine, state.Settings.Tts.PlaybackRate);
        if (!_shown.Accept(saved))
            return;
        var (read, shown) = (Read(), _shown.Value);
        _shown.Set(saved);
        Show(new Values(
            read.Engine == shown.Engine ? saved.Engine : read.Engine,
            read.Rate == shown.Rate ? saved.Rate : read.Rate));
    }

    protected override async Task<string?> SaveCoreAsync()
    {
        var values = Read();
        if (!Check(PlaybackRate, values.Rate is >= TtsSettingsDto.MinPlaybackRate and <= TtsSettingsDto.MaxPlaybackRate, "От 0,5 до 2"))
            return "Проверьте отмеченное поле.";

        var tts = Fields.With(State.Settings.Tts, engine: values.Engine, playbackRate: values.Rate);
        var result = await Client.UpdateSettingsAsync(tts: tts);
        if (!result.Ok)
            return Rejected(result);
        _shown.Saved(values);
        return null;
    }

    void Show(Values values) => Showing(() =>
    {
        EngineSystem.IsChecked = values.Engine == TtsEngineKind.System;
        EngineQwen.IsChecked = values.Engine == TtsEngineKind.Qwen;
        EngineOmni.IsChecked = values.Engine == TtsEngineKind.OmniVoice;
        PlaybackRate.Value = (decimal)values.Rate;
    });

    Values Read() => new(
        EngineOmni.IsChecked == true ? TtsEngineKind.OmniVoice : EngineQwen.IsChecked == true ? TtsEngineKind.Qwen : TtsEngineKind.System,
        PlaybackRate.Value is { } rate ? Math.Round((double)rate, 2) : 0);

    sealed record Values(TtsEngineKind Engine, double Rate);
}
