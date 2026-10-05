using Avalonia;
using Avalonia.Controls;
using MachineVoice.Protocol;

namespace MachineVoice.App;

/// <summary>
/// Settings pages with navigation on the left. Each page with settings has its own «Сохранить»; closing the window
/// with edits that are not saved asks what to do with them.
/// </summary>
public partial class SettingsWindow : Window
{
    // The page shown last in this run of the app.
    static int s_lastPage;

    readonly List<NavPage> _pages = [];
    readonly ControlState _state = new();
    bool _closing;
    bool _asking;

    /// <summary>For the XAML previewer only.</summary>
    public SettingsWindow()
    {
        InitializeComponent();
    }

    public SettingsWindow(IControlClient client, ControlState state, Action<string> log, bool openConnections) : this()
    {
        _state = state;
        var connections = new ConnectionsPage();
        _pages.AddRange(
        [
            new NavPage(new GeneralPage(), null),
            new NavPage(new VoicePage(), null),
            new NavPage(new SystemVoicePage(), TtsEngineKind.System),
            new NavPage(new QwenPage(), TtsEngineKind.Qwen),
            new NavPage(new OmniVoicePage(), TtsEngineKind.OmniVoice),
            new NavPage(new TextPage(), null),
            new NavPage(connections, null),
        ]);

        foreach (var nav in _pages)
        {
            nav.Page.Attach(client, state, log);
            nav.Page.Changed += RefreshNav;
            Nav.Items.Add(nav.Item);
        }

        RefreshNav();
        Nav.SelectionChanged += (_, _) => ShowPage(Nav.SelectedIndex);
        Nav.SelectedIndex = openConnections ? _pages.FindIndex(nav => nav.Page == connections) : Math.Clamp(s_lastPage, 0, _pages.Count - 1);

        Opened += async (_, _) =>
        {
            foreach (var nav in _pages)
                await nav.Page.LoadAsync();
        };
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            foreach (var nav in _pages)
                nav.Page.Close();
        };
    }

    public void Update(ControlState state)
    {
        foreach (var nav in _pages)
            nav.Page.Update(state);
        RefreshNav();
    }

    /// <summary>Closes without asking about edits, for quitting the app.</summary>
    public void CloseWithoutAsking()
    {
        _closing = true;
        Close();
    }

    void ShowPage(int index)
    {
        if (index < 0 || index >= _pages.Count)
            return;
        s_lastPage = index;
        PageHost.Content = _pages[index].Page;
    }

    void RefreshNav()
    {
        foreach (var nav in _pages)
        {
            var label = nav.Page.Title;
            if (nav.Engine is { } engine && engine == _state.Settings.Tts.Engine)
                label += " — основной";
            if (nav.Page.Edited)
                label += " •";
            nav.Label.Text = label;
        }
    }

    void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closing || !_pages.Any(nav => nav.Page.Edited))
            return;
        e.Cancel = true;
        if (!_asking)
            _ = AskAndCloseAsync();
    }

    async Task AskAndCloseAsync()
    {
        _asking = true;
        try
        {
            var edited = _pages.Where(nav => nav.Page.Edited).ToList();
            var choice = await CloseDialog.AskAsync(this, string.Join(", ", edited.Select(nav => $"«{nav.Page.Title}»")));
            if (choice == CloseChoice.Return)
                return;
            if (choice == CloseChoice.Save)
            {
                foreach (var nav in edited)
                {
                    if (!await nav.Page.SaveAsync())
                    {
                        Nav.SelectedIndex = _pages.IndexOf(nav);
                        return;
                    }
                }
            }

            CloseWithoutAsking();
        }
        finally
        {
            _asking = false;
        }
    }

    /// <param name="Engine">The engine that the page sets up, marked when it is the main one.</param>
    sealed class NavPage
    {
        public NavPage(SettingsPage page, TtsEngineKind? engine)
        {
            Page = page;
            Engine = engine;
            Label = new TextBlock { Margin = new Thickness(engine is null ? 0 : 16, 0, 0, 0) };
            Item = new ListBoxItem { Content = Label };
        }

        public SettingsPage Page { get; }
        public TtsEngineKind? Engine { get; }
        public TextBlock Label { get; }
        public ListBoxItem Item { get; }
    }
}

enum CloseChoice
{
    Return,
    Save,
    Discard,
}

sealed class CloseDialog : Window
{
    CloseDialog(string pages)
    {
        Title = "MachineVoice";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var back = new Button { Content = "Вернуться" };
        var discard = new Button { Content = "Не сохранять" };
        var save = new Button { Content = "Сохранить", Classes = { "accent" } };
        back.Click += (_, _) => Close(CloseChoice.Return);
        discard.Click += (_, _) => Close(CloseChoice.Discard);
        save.Click += (_, _) => Close(CloseChoice.Save);

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = $"Есть несохранённые изменения: {pages}. Сохранить их?", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { back, discard, save },
                },
            },
        };
    }

    /// <summary>Closing the dialog itself means <see cref="CloseChoice.Return"/>.</summary>
    public static Task<CloseChoice> AskAsync(Window owner, string pages) => new CloseDialog(pages).ShowDialog<CloseChoice>(owner);
}
