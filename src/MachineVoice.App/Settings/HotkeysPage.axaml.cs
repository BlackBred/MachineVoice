using Avalonia.Input;
using MachineVoice.Protocol;

namespace MachineVoice.App;

public partial class HotkeysPage : SettingsPage
{
    readonly Shown<Values> _shown = new(Values.Default);
    bool _recording;
    int _keyCode = HotkeyChordDto.DefaultKeyCode;
    int _modifiers = HotkeyChordDto.DefaultModifiers;

    public HotkeysPage()
    {
        InitializeComponent();
        Watch(Interrupt);
        UseSaveBar(SaveButton, SaveStatus);
        RecordButton.Click += (_, _) => StartRecording();
        KeyDown += OnKeyDown;
        Show(_shown.Value);
    }

    public override string Title => "Горячие клавиши";

    public override bool Edited => Read() != _shown.Value;

    public override void Update(ControlState state)
    {
        var hotkeys = HotkeyRules.Canonical(state.Settings.Hotkeys);
        var saved = new Values(hotkeys.Interrupt, hotkeys.Clipboard.KeyCode, hotkeys.Clipboard.Modifiers);
        if (!_shown.Accept(saved))
            return;
        var (read, shown) = (Read(), _shown.Value);
        var chord = read.KeyCode == shown.KeyCode && read.Modifiers == shown.Modifiers;
        _shown.Set(saved);
        Show(new Values(
            read.Interrupt == shown.Interrupt ? saved.Interrupt : read.Interrupt,
            chord ? saved.KeyCode : read.KeyCode,
            chord ? saved.Modifiers : read.Modifiers));
    }

    protected override async Task<string?> SaveCoreAsync()
    {
        var values = Read();
        var result = await Client.UpdateSettingsAsync(hotkeys: values.ToSettings());
        if (!result.Ok)
            return Rejected(result);
        _shown.Saved(values);
        return null;
    }

    void Show(Values values) => Showing(() =>
    {
        _recording = false;
        _keyCode = values.KeyCode;
        _modifiers = values.Modifiers;
        Interrupt.IsChecked = values.Interrupt;
        ChordText.Text = HotkeyText.Format(values.KeyCode, values.Modifiers);
        RecordButton.Content = "Записать";
        RecordHint.Text = "Нажмите «Записать» и сочетание. Нужен хотя бы один модификатор, иначе клавиша перехватит набор текста.";
    });

    Values Read() => new(Interrupt.IsChecked == true, _keyCode, _modifiers);

    void StartRecording()
    {
        _recording = true;
        RecordButton.Content = "Нажмите сочетание…";
        RecordHint.Text = "Удерживайте модификатор и нажмите клавишу. Escape отменяет.";
        Focus();
    }

    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_recording)
            return;

        e.Handled = true;
        if (e.Key == Key.Escape)
        {
            Show(Read());
            return;
        }

        if (HotkeyText.IsModifier(e.Key))
            return;

        var modifiers = HotkeyText.Modifiers(e.KeyModifiers);
        if (modifiers == 0)
        {
            RecordHint.Text = "Нужен хотя бы один модификатор: ⌃, ⌥, ⇧ или ⌘.";
            return;
        }

        if (!HotkeyText.TryCode(e.Key, out var code))
        {
            RecordHint.Text = "Эту клавишу назначить нельзя.";
            return;
        }

        _keyCode = code;
        _modifiers = modifiers;
        _recording = false;
        ChordText.Text = HotkeyText.Format(code, modifiers);
        RecordButton.Content = "Записать";
        RecordHint.Text = "Нажмите «Записать» и сочетание. Нужен хотя бы один модификатор, иначе клавиша перехватит набор текста.";
        Refresh();
    }

    sealed record Values(bool Interrupt, int KeyCode, int Modifiers)
    {
        public static Values Default { get; } = new(false, HotkeyChordDto.DefaultKeyCode, HotkeyChordDto.DefaultModifiers);

        public HotkeySettingsDto ToSettings() => new()
        {
            Interrupt = Interrupt,
            Clipboard = new HotkeyChordDto { KeyCode = KeyCode, Modifiers = Modifiers },
        };
    }
}
