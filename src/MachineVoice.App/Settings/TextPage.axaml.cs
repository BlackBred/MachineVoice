using MachineVoice.Protocol;

namespace MachineVoice.App;

/// <summary>Everything that goes through an LLM before the text is spoken; for now the retelling.</summary>
public partial class TextPage : SettingsPage
{
    readonly Shown<Values> _shown = new(Values.Of(new SummarySettingsDto()));

    public TextPage()
    {
        InitializeComponent();
        Watch(Enabled, Endpoint, Model, ApiKey, Timeout);
        UseSaveBar(SaveButton, SaveStatus);
        Show(_shown.Value);
    }

    public override string Title => "Обработка текста";

    public override bool Edited => Read() != _shown.Value;

    public override void Update(ControlState state)
    {
        var saved = Values.Of(state.Settings.Summary);
        if (!_shown.Accept(saved))
            return;
        var (read, shown) = (Read(), _shown.Value);
        _shown.Set(saved);
        Show(new Values(
            read.Enabled == shown.Enabled ? saved.Enabled : read.Enabled,
            read.Endpoint == shown.Endpoint ? saved.Endpoint : read.Endpoint,
            read.Model == shown.Model ? saved.Model : read.Model,
            read.ApiKey == shown.ApiKey ? saved.ApiKey : read.ApiKey,
            read.Timeout == shown.Timeout ? saved.Timeout : read.Timeout));
    }

    protected override async Task<string?> SaveCoreAsync()
    {
        var values = Read();
        var valid = Check(Endpoint, Fields.ValidEndpoint(values.Endpoint), "Нужен адрес http:// или https://")
            & Check(Model, !values.Enabled || values.Model.Length > 0, "Укажите модель для пересказа")
            & Check(Timeout, values.Timeout is >= 1 and <= SummarySettingsDto.MaxTimeoutSeconds, $"От 1 до {SummarySettingsDto.MaxTimeoutSeconds} с");
        if (!valid)
            return "Проверьте отмеченные поля.";

        var summary = new SummarySettingsDto
        {
            Enabled = values.Enabled,
            Endpoint = values.Endpoint,
            Model = values.Model,
            ApiKey = values.ApiKey.Length == 0 ? null : values.ApiKey,
            TimeoutSeconds = values.Timeout,
        };
        var result = await Client.UpdateSettingsAsync(summary: summary);
        if (!result.Ok)
            return Rejected(result);
        _shown.Saved(values);
        return null;
    }

    void Show(Values values) => Showing(() =>
    {
        Enabled.IsChecked = values.Enabled;
        Endpoint.Text = values.Endpoint;
        Model.Text = values.Model;
        ApiKey.Text = values.ApiKey;
        Timeout.Value = values.Timeout >= 0 ? values.Timeout : null;
    });

    Values Read() => new(
        Enabled.IsChecked == true,
        Fields.Text(Endpoint, SummarySettingsDto.DefaultEndpoint),
        Fields.Text(Model),
        Fields.Text(ApiKey),
        Fields.Whole(Timeout));

    sealed record Values(bool Enabled, string Endpoint, string Model, string ApiKey, int Timeout)
    {
        public static Values Of(SummarySettingsDto summary) =>
            new(summary.Enabled, summary.Endpoint, summary.Model, summary.ApiKey ?? "", summary.TimeoutSeconds);
    }
}
