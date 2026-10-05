using MachineVoice.Protocol;

namespace MachineVoice.App;

public partial class SystemVoicePage : SettingsPage
{
    readonly Shown<double> _shown = new(SystemVoiceSettingsDto.DefaultRate);

    public SystemVoicePage()
    {
        InitializeComponent();
        Watch(Rate);
        UseSaveBar(SaveButton, SaveStatus);
        Show(_shown.Value);
    }

    public override string Title => "Голос macOS";

    public override bool Edited => Read() != _shown.Value;

    public override void Update(ControlState state)
    {
        var saved = state.Settings.Tts.SystemVoice.Rate;
        if (!_shown.Accept(saved))
            return;
        var edited = Edited;
        _shown.Set(saved);
        if (edited)
            Refresh();
        else
            Show(saved);
    }

    protected override async Task<string?> SaveCoreAsync()
    {
        var rate = Read();
        if (!Check(Rate, rate is >= 0 and <= 1, "От 0 до 1"))
            return "Проверьте отмеченное поле.";

        var tts = Fields.With(State.Settings.Tts, systemVoice: new SystemVoiceSettingsDto { Rate = rate });
        var result = await Client.UpdateSettingsAsync(tts: tts);
        if (!result.Ok)
            return Rejected(result);
        _shown.Saved(rate);
        return null;
    }

    void Show(double rate) => Showing(() => Rate.Value = (decimal)rate);

    double Read() => Rate.Value is { } rate ? Math.Round((double)rate, 2) : -1;
}
