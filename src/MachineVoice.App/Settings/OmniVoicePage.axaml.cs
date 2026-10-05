using MachineVoice.Platform.MacOS;
using MachineVoice.Protocol;

namespace MachineVoice.App;

/// <summary>
/// OmniVoice and its voice studio. Making, saving and deleting a voice happen at once; a saved voice is selected in
/// the field and reads once the page is saved.
/// </summary>
public partial class OmniVoicePage : SettingsPage
{
    readonly Shown<Values> _shown = new(Values.Of(new OmniVoiceTtsSettingsDto()));
    List<VoiceDto> _voices = [];
    bool _voicesLoaded;
    VoiceDto? _draft;
    AvAudioPlayer? _preview;
    bool _confirmDelete;

    public OmniVoicePage()
    {
        InitializeComponent();
        Watch(Voice, Language, Endpoint, Model, Unload);
        UseSaveBar(SaveButton, SaveStatus);
        Voice.SelectionChanged += (_, _) =>
        {
            _confirmDelete = false;
            UpdateVoiceButtons();
        };
        PlayButton.Click += (_, _) => Play(SelectedVoice());
        DeleteButton.Click += async (_, _) => await DeleteVoiceAsync();
        NewVoiceButton.Click += async (_, _) => await NewVoiceAsync();
        SaveDraftButton.Click += async (_, _) => await SaveDraftAsync();
        PlayDraftButton.Click += (_, _) => Play(_draft);
        Show(_shown.Value);
    }

    public override string Title => "OmniVoice";

    public override bool Edited => Read() != _shown.Value;

    public override void Update(ControlState state)
    {
        var saved = Values.Of(state.Settings.Tts.OmniVoice);
        if (!_shown.Accept(saved))
            return;
        var (read, shown) = (Read(), _shown.Value);
        _shown.Set(saved);
        Show(new Values(
            read.Voice == shown.Voice ? saved.Voice : read.Voice,
            read.Language == shown.Language ? saved.Language : read.Language,
            read.Endpoint == shown.Endpoint ? saved.Endpoint : read.Endpoint,
            read.Model == shown.Model ? saved.Model : read.Model,
            read.Unload == shown.Unload ? saved.Unload : read.Unload));
    }

    public override async Task LoadAsync()
    {
        try
        {
            var result = await Client.ListVoicesAsync();
            if (result is { Ok: true, Voices: { } voices })
            {
                _voicesLoaded = true;
                Showing(() => ShowVoices(voices, SelectedVoiceId()));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log($"Voice list failed: {ex.Message}");
        }
    }

    public override void Close()
    {
        _preview?.Dispose();
        _preview = null;
    }

    protected override async Task<string?> SaveCoreAsync()
    {
        var values = Read();
        var valid = Check(Language, ValidLanguage(values.Language), "Код языка из латинских букв, например ru")
            & Check(Endpoint, Fields.ValidEndpoint(values.Endpoint), "Нужен адрес http:// или https://")
            & Check(Unload, values.Unload >= 0, "Укажите число минут");
        if (!valid)
            return "Проверьте отмеченные поля.";

        var omni = new OmniVoiceTtsSettingsDto
        {
            Endpoint = values.Endpoint,
            Model = values.Model,
            Voice = values.Voice,
            Language = values.Language,
            UnloadAfterMinutes = values.Unload,
        };
        var result = await Client.UpdateSettingsAsync(tts: Fields.With(State.Settings.Tts, omniVoice: omni));
        if (!result.Ok)
            return Rejected(result);
        _shown.Saved(values);
        return null;
    }

    void Show(Values values) => Showing(() =>
    {
        ShowVoices(_voices, values.Voice);
        Language.Text = values.Language;
        Endpoint.Text = values.Endpoint;
        Model.Text = values.Model;
        Unload.Value = values.Unload >= 0 ? values.Unload : null;
    });

    /// <summary>The settings may name a voice that is gone; it stays selectable so that saving keeps it.</summary>
    void ShowVoices(List<VoiceDto> voices, string selected)
    {
        _voices = voices;
        var items = new List<VoiceItem> { new("", "Случайный (новый в каждой фразе)") };
        items.AddRange(voices.Select(voice => new VoiceItem(voice.Id, voice.Name)));
        if (selected.Length > 0 && voices.All(voice => voice.Id != selected))
            items.Add(new VoiceItem(selected, _voicesLoaded ? "Удалённый голос" : "…"));
        Voice.ItemsSource = items;
        Voice.SelectedItem = items.First(item => item.Id == selected);
        UpdateVoiceButtons();
    }

    string SelectedVoiceId() => (Voice.SelectedItem as VoiceItem)?.Id ?? _shown.Value.Voice;

    VoiceDto? SelectedVoice() => _voices.Find(voice => voice.Id == SelectedVoiceId());

    void UpdateVoiceButtons()
    {
        var voice = SelectedVoice();
        PlayButton.IsEnabled = voice is not null;
        DeleteButton.IsEnabled = voice is { BuiltIn: false };
        DeleteButton.Content = _confirmDelete ? "Точно удалить?" : "Удалить";
    }

    void Play(VoiceDto? voice)
    {
        if (voice is null)
            return;
        try
        {
            _preview ??= new AvAudioPlayer();
            _preview.Play(File.ReadAllBytes(voice.AudioPath));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Log($"Voice preview failed: {ex.Message}");
            StudioStatus.Text = "Не удалось проиграть образец: " + ex.Message;
        }
    }

    // The first click asks, the second deletes: a random voice cannot be made again.
    async Task DeleteVoiceAsync()
    {
        if (SelectedVoice() is not { } voice)
            return;
        if (!_confirmDelete)
        {
            _confirmDelete = true;
            UpdateVoiceButtons();
            return;
        }

        _confirmDelete = false;
        DeleteButton.IsEnabled = false;
        try
        {
            var result = await Client.DeleteVoiceAsync(voice.Id);
            if (result is not { Ok: true, Voices: { } voices })
            {
                StudioStatus.Text = "Не удалось удалить: " + result.Error;
                return;
            }

            // Deleting the saved voice resets the setting in the core; the field follows it.
            var saved = _shown.Value.Voice;
            ShowVoices(voices, saved != voice.Id && voices.Any(v => v.Id == saved) ? saved : "");
            StudioStatus.Text = $"Голос «{voice.Name}» удалён";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log($"Voice delete failed: {ex}");
            StudioStatus.Text = "Не удалось удалить: " + ex.Message;
        }
        finally
        {
            UpdateVoiceButtons();
        }
    }

    async Task NewVoiceAsync()
    {
        NewVoiceButton.IsEnabled = false;
        DraftPanel.IsVisible = false;
        _draft = null;
        StudioStatus.Text = "Придумываю голос… В первый раз запускается сервер и загружается модель (около 2 ГБ), это может занять несколько минут.";
        try
        {
            var result = await Client.CreateVoiceAsync();
            if (result is { Ok: true, Voice: { } draft })
            {
                _draft = draft;
                DraftName.Text = $"Голос {_voices.Count(voice => !voice.BuiltIn) + 1}";
                DraftPanel.IsVisible = true;
                StudioStatus.Text = "Вот так он звучит. Сохраните его или придумайте другой.";
                Play(draft);
                return;
            }

            StudioStatus.Text = result.Error == ProtocolErrors.Unavailable
                ? "Сервер OmniVoice не ответил: " + result.Detail
                : "Не удалось создать голос: " + result.Error;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log($"Voice draft failed: {ex}");
            StudioStatus.Text = "Не удалось создать голос: " + ex.Message;
        }
        finally
        {
            NewVoiceButton.IsEnabled = true;
            NewVoiceButton.Content = _draft is null ? "Придумать голос" : "Придумать другой";
        }
    }

    async Task SaveDraftAsync()
    {
        if (_draft is not { } draft)
            return;
        var name = DraftName.Text?.Trim() ?? "";
        if (!Check(DraftName, name.Length > 0, "Дайте голосу название"))
            return;

        SaveDraftButton.IsEnabled = false;
        try
        {
            var result = await Client.SaveVoiceAsync(draft.Id, name);
            if (result is not { Ok: true, Voices: { } voices, Voice: { } saved })
            {
                StudioStatus.Text = "Не удалось сохранить голос: " + result.Error;
                return;
            }

            _draft = null;
            DraftPanel.IsVisible = false;
            NewVoiceButton.Content = "Придумать голос";
            ShowVoices(voices, saved.Id);
            StudioStatus.Text = $"Голос «{saved.Name}» сохранён и выбран. Нажмите «Сохранить» внизу, чтобы OmniVoice читал им.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log($"Voice save failed: {ex}");
            StudioStatus.Text = "Не удалось сохранить голос: " + ex.Message;
        }
        finally
        {
            SaveDraftButton.IsEnabled = true;
        }
    }

    static bool ValidLanguage(string language) =>
        language.Length is > 0 and <= 16 && language.All(c => char.IsAsciiLetter(c) || c == '-');

    Values Read() => new(
        SelectedVoiceId(),
        Fields.Text(Language, OmniVoiceTtsSettingsDto.DefaultLanguage).ToLowerInvariant(),
        Fields.Text(Endpoint, QwenTtsSettingsDto.DefaultEndpoint),
        Fields.Text(Model, OmniVoiceTtsSettingsDto.DefaultModel),
        Fields.Whole(Unload));

    sealed record Values(string Voice, string Language, string Endpoint, string Model, int Unload)
    {
        public static Values Of(OmniVoiceTtsSettingsDto omni) =>
            new(omni.Voice, omni.Language, omni.Endpoint, omni.Model, omni.UnloadAfterMinutes);
    }

    sealed record VoiceItem(string Id, string Label)
    {
        public override string ToString() => Label;
    }
}
