using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using MachineVoice.Protocol;

namespace MachineVoice.App;

/// <summary>
/// One page of the settings window. A page with settings keeps the user's edits until its own «Сохранить»; values
/// changed elsewhere (the menu, the overlay, the MCP tool) replace only fields that the user has not edited.
/// </summary>
public class SettingsPage : UserControl
{
    Button? _save;
    TextBlock? _status;
    bool _showing;
    bool _saving;

    protected IControlClient Client { get; private set; } = null!;
    protected ControlState State { get; private set; } = new();
    protected Action<string> Log { get; private set; } = _ => { };

    public virtual string Title => "";

    /// <summary>The page has edits that are not saved.</summary>
    public virtual bool Edited => false;

    /// <summary>Edits came or went, or the page wants its navigation label redrawn.</summary>
    public event Action? Changed;

    protected virtual string SavedMessage => "Сохранено";

    public void Attach(IControlClient client, ControlState state, Action<string> log)
    {
        Client = client;
        State = state;
        Log = log;
        Update(state);
    }

    public virtual void Update(ControlState state)
    {
    }

    /// <summary>Called when the window opens: statuses and lists that are not in <see cref="ControlState"/>.</summary>
    public virtual Task LoadAsync() => Task.CompletedTask;

    public virtual void Close()
    {
    }

    /// <returns>False when nothing was saved; the page shows why.</returns>
    public async Task<bool> SaveAsync()
    {
        if (!Edited || _saving)
            return !Edited;

        _saving = true;
        Refresh();
        SetStatus("");
        try
        {
            var error = await SaveCoreAsync();
            SetStatus(error ?? SavedMessage);
            return error is null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log($"{Title}: save failed: {ex}");
            SetStatus("Не удалось сохранить: " + ex.Message);
            return false;
        }
        finally
        {
            _saving = false;
            Refresh();
        }
    }

    /// <returns>Null when saved, otherwise what to tell the user.</returns>
    protected virtual Task<string?> SaveCoreAsync() => Task.FromResult<string?>(null);

    protected void UseSaveBar(Button save, TextBlock status)
    {
        _save = save;
        _status = status;
        save.Click += async (_, _) => await SaveAsync();
        Refresh();
    }

    protected void SetStatus(string text)
    {
        if (_status is not null)
            _status.Text = text;
    }

    /// <summary>A change of any of <paramref name="fields"/> by the user is an edit.</summary>
    protected void Watch(params Control[] fields)
    {
        foreach (var field in fields)
        {
            switch (field)
            {
                case TextBox box:
                    box.TextChanged += (_, _) => OnFieldChanged(field);
                    break;
                case NumericUpDown number:
                    number.ValueChanged += (_, _) => OnFieldChanged(field);
                    break;
                case ComboBox combo:
                    combo.SelectionChanged += (_, _) => OnFieldChanged(field);
                    break;
                case ToggleButton toggle:
                    toggle.IsCheckedChanged += (_, _) => OnFieldChanged(field);
                    break;
            }
        }
    }

    /// <summary>Fills fields from the settings: not an edit.</summary>
    protected void Showing(Action show)
    {
        _showing = true;
        try
        {
            show();
        }
        finally
        {
            _showing = false;
        }

        Refresh();
    }

    /// <summary>Marks <paramref name="field"/> when <paramref name="valid"/> is false.</summary>
    protected static bool Check(Control field, bool valid, string message)
    {
        if (valid)
            DataValidationErrors.ClearErrors(field);
        else
            DataValidationErrors.SetErrors(field, [message]);
        return valid;
    }

    protected static string Rejected(ResultMessage result) => result.Error == ProtocolErrors.InvalidArgument
        ? "MachineVoice не принял значения: проверьте поля."
        : "Не удалось сохранить: " + result.Error;

    protected void Refresh()
    {
        if (_save is not null)
            _save.IsEnabled = Edited && !_saving;
        Changed?.Invoke();
    }

    void OnFieldChanged(Control field)
    {
        if (_showing)
            return;
        DataValidationErrors.ClearErrors(field);
        SetStatus("");
        Refresh();
    }
}

/// <summary>
/// The saved values a page shows. Right after a save the core may still report the values from before it; those
/// are not shown again.
/// </summary>
sealed class Shown<T>(T initial) where T : notnull
{
    T? _before;
    bool _saving;

    public T Value { get; private set; } = initial;

    /// <summary>The core reports <paramref name="saved"/>: whether the page should show it.</summary>
    public bool Accept(T saved)
    {
        if (_saving)
        {
            if (saved.Equals(_before))
                return false;
            _saving = false;
        }

        return !saved.Equals(Value);
    }

    public void Set(T value)
    {
        Value = value;
        _saving = false;
    }

    public void Saved(T value)
    {
        _before = Value;
        _saving = true;
        Value = value;
    }
}

static class Fields
{
    public static bool ValidEndpoint(string endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    public static string Text(TextBox box, string fallback = "") =>
        string.IsNullOrWhiteSpace(box.Text) ? fallback : box.Text.Trim();

    /// <summary>-1 for an empty field, which no setting allows.</summary>
    public static int Whole(NumericUpDown number) => number.Value is { } value ? (int)value : -1;

    public static TtsSettingsDto With(
        TtsSettingsDto tts,
        TtsEngineKind? engine = null,
        double? playbackRate = null,
        SystemVoiceSettingsDto? systemVoice = null,
        QwenTtsSettingsDto? qwen = null,
        OmniVoiceTtsSettingsDto? omniVoice = null) => new()
    {
        Engine = engine ?? tts.Engine,
        PlaybackRate = playbackRate ?? tts.PlaybackRate,
        SystemVoice = systemVoice ?? tts.SystemVoice,
        Qwen = qwen ?? tts.Qwen,
        OmniVoice = omniVoice ?? tts.OmniVoice,
    };
}
