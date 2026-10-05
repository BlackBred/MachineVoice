using MachineVoice.Protocol;

namespace MachineVoice.App;

public partial class QwenPage : SettingsPage
{
    readonly Shown<Values> _shown = new(Values.Of(new QwenTtsSettingsDto()));

    public QwenPage()
    {
        InitializeComponent();
        Watch(Voice, Endpoint, Model, Unload);
        UseSaveBar(SaveButton, SaveStatus);
        Show(_shown.Value);
    }

    public override string Title => "Qwen3-TTS";

    public override bool Edited => Read() != _shown.Value;

    public override void Update(ControlState state)
    {
        var saved = Values.Of(state.Settings.Tts.Qwen);
        if (!_shown.Accept(saved))
            return;
        var (read, shown) = (Read(), _shown.Value);
        _shown.Set(saved);
        Show(new Values(
            read.Voice == shown.Voice ? saved.Voice : read.Voice,
            read.Endpoint == shown.Endpoint ? saved.Endpoint : read.Endpoint,
            read.Model == shown.Model ? saved.Model : read.Model,
            read.Unload == shown.Unload ? saved.Unload : read.Unload));
    }

    protected override async Task<string?> SaveCoreAsync()
    {
        var values = Read();
        var valid = Check(Endpoint, Fields.ValidEndpoint(values.Endpoint), "Нужен адрес http:// или https://")
            & Check(Unload, values.Unload >= 0, "Укажите число минут");
        if (!valid)
            return "Проверьте отмеченные поля.";

        var qwen = new QwenTtsSettingsDto
        {
            Endpoint = values.Endpoint,
            Model = values.Model,
            Voice = values.Voice,
            UnloadAfterMinutes = values.Unload,
        };
        var result = await Client.UpdateSettingsAsync(tts: Fields.With(State.Settings.Tts, qwen: qwen));
        if (!result.Ok)
            return Rejected(result);
        _shown.Saved(values);
        return null;
    }

    void Show(Values values) => Showing(() =>
    {
        var voices = QwenTtsSettingsDto.Voices.ToList();
        if (!voices.Contains(values.Voice))
            voices.Insert(0, values.Voice);
        Voice.ItemsSource = voices;
        Voice.SelectedItem = values.Voice;
        Endpoint.Text = values.Endpoint;
        Model.Text = values.Model;
        Unload.Value = values.Unload >= 0 ? values.Unload : null;
    });

    Values Read() => new(
        Voice.SelectedItem as string ?? QwenTtsSettingsDto.DefaultVoice,
        Fields.Text(Endpoint, QwenTtsSettingsDto.DefaultEndpoint),
        Fields.Text(Model, QwenTtsSettingsDto.DefaultModel),
        Fields.Whole(Unload));

    sealed record Values(string Voice, string Endpoint, string Model, int Unload)
    {
        public static Values Of(QwenTtsSettingsDto qwen) => new(qwen.Voice, qwen.Endpoint, qwen.Model, qwen.UnloadAfterMinutes);
    }
}
