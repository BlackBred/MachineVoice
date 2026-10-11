using Avalonia.Threading;
using MachineVoice.Protocol;

namespace MachineVoice.App;

/// <summary>Everything that goes through an LLM before the text is spoken; for now the retelling.</summary>
public partial class TextPage : SettingsPage
{
    readonly Shown<Values> _shown = new(Values.Of(new SummarySettingsDto()));
    readonly DispatcherTimer _probeDelay;
    CancellationTokenSource? _probeCancel;
    int _probeGeneration;
    bool _filling;

    public TextPage()
    {
        InitializeComponent();
        Watch(Enabled, LengthLimit, PreferFull, PreferSummary, Endpoint, Model, ApiKey, Timeout, SystemPrompt);
        UseSaveBar(SaveButton, SaveStatus);
        _probeDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _probeDelay.Tick += async (_, _) =>
        {
            _probeDelay.Stop();
            await ProbeAsync();
        };
        Endpoint.TextChanged += (_, _) => ScheduleProbe();
        Model.TextChanged += (_, _) => ScheduleProbe();
        ApiKey.TextChanged += (_, _) => ScheduleProbe();
        Show(_shown.Value);
    }

    public override string Title => "Обработка текста";

    public override bool Edited => Read() != _shown.Value;

    public override Task LoadAsync()
    {
        ScheduleProbe();
        return Task.CompletedTask;
    }

    public override void Close()
    {
        _probeDelay.Stop();
        _probeCancel?.Cancel();
        _probeGeneration++;
    }

    public override void Update(ControlState state)
    {
        var saved = Values.Of(state.Settings.Summary);
        if (!_shown.Accept(saved))
            return;
        var (read, shown) = (Read(), _shown.Value);
        _shown.Set(saved);
        Show(new Values(
            read.Enabled == shown.Enabled ? saved.Enabled : read.Enabled,
            read.LengthLimit == shown.LengthLimit ? saved.LengthLimit : read.LengthLimit,
            read.DefaultReading == shown.DefaultReading ? saved.DefaultReading : read.DefaultReading,
            read.Endpoint == shown.Endpoint ? saved.Endpoint : read.Endpoint,
            read.Model == shown.Model ? saved.Model : read.Model,
            read.ApiKey == shown.ApiKey ? saved.ApiKey : read.ApiKey,
            read.Timeout == shown.Timeout ? saved.Timeout : read.Timeout,
            read.SystemPrompt == shown.SystemPrompt ? saved.SystemPrompt : read.SystemPrompt));
    }

    protected override async Task<string?> SaveCoreAsync()
    {
        var values = Read();
        var valid = Check(Endpoint, Fields.ValidEndpoint(values.Endpoint), "Нужен адрес http:// или https://")
            & Check(Model, !values.Enabled || values.Model.Length > 0, "Укажите модель для пересказа")
            & Check(LengthLimit, values.LengthLimit is >= 0 and <= SummarySettingsDto.MaxLengthLimit, $"От 0 до {SummarySettingsDto.MaxLengthLimit}")
            & Check(Timeout, values.Timeout is >= 1 and <= SummarySettingsDto.MaxTimeoutSeconds, $"От 1 до {SummarySettingsDto.MaxTimeoutSeconds} с")
            & Check(SystemPrompt, values.SystemPrompt.Length <= SummarySettingsDto.MaxSystemPrompt, $"Не длиннее {SummarySettingsDto.MaxSystemPrompt} символов");
        if (!valid)
            return "Проверьте отмеченные поля.";

        var summary = new SummarySettingsDto
        {
            Enabled = values.Enabled,
            Endpoint = values.Endpoint,
            Model = values.Model,
            ApiKey = values.ApiKey.Length == 0 ? null : values.ApiKey,
            TimeoutSeconds = values.Timeout,
            LengthLimit = values.LengthLimit,
            DefaultReading = values.DefaultReading,
            SystemPrompt = values.SystemPrompt,
        };
        var result = await Client.UpdateSettingsAsync(summary: summary);
        if (!result.Ok)
            return Rejected(result);
        _shown.Saved(values);
        return null;
    }

    void Show(Values values)
    {
        var before = Read();
        _filling = true;
        try
        {
            Showing(() =>
            {
                Enabled.IsChecked = values.Enabled;
                Endpoint.Text = values.Endpoint;
                Model.Text = values.Model;
                ApiKey.Text = values.ApiKey;
                Timeout.Value = values.Timeout >= 0 ? values.Timeout : null;
                LengthLimit.Value = values.LengthLimit >= 0 ? values.LengthLimit : null;
                PreferFull.IsChecked = values.DefaultReading == ReadingPreference.Full;
                PreferSummary.IsChecked = values.DefaultReading == ReadingPreference.Summary;
                SystemPrompt.Text = values.SystemPrompt;
            });
        }
        finally
        {
            _filling = false;
        }

        if (before.Endpoint != values.Endpoint || before.Model != values.Model || before.ApiKey != values.ApiKey)
            ScheduleProbe();
    }

    void ScheduleProbe()
    {
        if (_filling || Client is null)
            return;

        var next = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _probeCancel, next);
        previous?.Cancel();
        _probeGeneration++;
        ShowAvailability("Проверяется…", "");
        _probeDelay.Stop();
        _probeDelay.Start();
    }

    async Task ProbeAsync()
    {
        var generation = _probeGeneration;
        if (_probeCancel is not { } probe)
            return;
        var token = probe.Token;
        if (token.IsCancellationRequested)
            return;
        var values = Read();
        if (!Fields.ValidEndpoint(values.Endpoint))
        {
            if (generation == _probeGeneration)
                ShowAvailability("Нужен адрес http:// или https://", "bad");
            return;
        }

        try
        {
            var result = await Client.ProbeSummaryAsync(
                values.Endpoint,
                values.Model,
                values.ApiKey.Length == 0 ? null : values.ApiKey,
                token);
            if (generation != _probeGeneration || token.IsCancellationRequested)
                return;
            if (result is { Ok: true, SummaryProbe: { } status })
                ShowAvailability(Describe(status.Status));
            else
                ShowAvailability("Не удалось проверить", "bad");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (generation != _probeGeneration)
                return;
            Log($"{Title}: probe failed: {ex.Message}");
            ShowAvailability("Не удалось проверить", "bad");
        }
    }

    static (string Text, string Kind) Describe(SummaryProbeStatus status) => status switch
    {
        SummaryProbeStatus.Available => ("Модель доступна", "ok"),
        SummaryProbeStatus.Reachable => ("Сервер доступен, укажите модель", "warn"),
        SummaryProbeStatus.Missing => ("Такой модели на сервере нет", "warn"),
        SummaryProbeStatus.Unauthorized => ("Ключ API не принят", "bad"),
        _ => ("Сервер недоступен", "bad"),
    };

    void ShowAvailability(string text, string kind)
    {
        ModelStatus.Text = text;
        ModelStatus.Classes.Set("hint", kind.Length == 0);
        ModelStatus.Classes.Set("ok", kind == "ok");
        ModelStatus.Classes.Set("warning", kind == "warn");
        ModelStatus.Classes.Set("error", kind == "bad");
        ModelLamp.Classes.Set("ok", kind == "ok");
        ModelLamp.Classes.Set("warn", kind == "warn");
        ModelLamp.Classes.Set("bad", kind == "bad");
    }

    void ShowAvailability((string Text, string Kind) status) => ShowAvailability(status.Text, status.Kind);

    Values Read() => new(
        Enabled.IsChecked == true,
        Fields.Whole(LengthLimit),
        PreferFull.IsChecked == true ? ReadingPreference.Full : ReadingPreference.Summary,
        Fields.Text(Endpoint, SummarySettingsDto.DefaultEndpoint),
        Fields.Text(Model),
        Fields.Text(ApiKey),
        Fields.Whole(Timeout),
        Fields.Text(SystemPrompt, SummarySettingsDto.DefaultSystemPrompt));

    sealed record Values(
        bool Enabled,
        int LengthLimit,
        ReadingPreference DefaultReading,
        string Endpoint,
        string Model,
        string ApiKey,
        int Timeout,
        string SystemPrompt)
    {
        public static Values Of(SummarySettingsDto summary) =>
            new(
                summary.Enabled,
                summary.LengthLimit,
                summary.DefaultReading,
                summary.Endpoint,
                summary.Model,
                summary.ApiKey ?? "",
                summary.TimeoutSeconds,
                string.IsNullOrWhiteSpace(summary.SystemPrompt)
                    ? SummarySettingsDto.DefaultSystemPrompt
                    : summary.SystemPrompt.Trim());
    }
}
