using MachineVoice.Protocol;

namespace MachineVoice.App;

/// <summary>Cursor hooks and the MCP entry. Only actions here, which apply at once: there is nothing to save.</summary>
public partial class ConnectionsPage : SettingsPage
{
    public const string CursorSource = "cursor";

    bool _busy;

    public ConnectionsPage()
    {
        InitializeComponent();
        CursorButton.Click += async (_, _) => await ToggleCursorAsync();
        McpButton.Click += async (_, _) => await ToggleMcpAsync();
    }

    public override string Title => "Подключения";

    public override void Update(ControlState state)
    {
        state.Sources.TryGetValue(CursorSource, out var cursor);
        var cursorStatus = cursor?.Status ?? SourceConnectionStatus.Disconnected;
        CursorStatusText.Text = Labels.CursorStatus(cursorStatus);
        CursorButton.Content = cursorStatus == SourceConnectionStatus.Connected ? "Отключить Cursor" : "Подключить Cursor";
        CursorButton.IsEnabled = !_busy;
        LegacyWarning.IsVisible = cursor?.LegacySpeaker == true;

        if (state.Mcp is { } mcp)
        {
            McpStatusText.Text = Labels.McpStatus(mcp);
            McpButton.Content = DisconnectsMcp(mcp) ? "Отключить MCP от Cursor" : "Подключить MCP к Cursor";
            McpButton.IsEnabled = !_busy && (DisconnectsMcp(mcp) || mcp.Available);
            if (mcp.Status != SourceConnectionStatus.Connected)
                McpHint.IsVisible = false;
        }
        else
        {
            McpStatusText.Text = "Проверяется…";
            McpButton.Content = "Подключить MCP к Cursor";
            McpButton.IsEnabled = false;
        }
    }

    public override async Task LoadAsync()
    {
        try
        {
            var cursor = await Client.GetSourceStatusAsync(CursorSource);
            if (cursor is { Ok: true, Source: { } source })
                State.SetSource(source);
            var mcp = await Client.GetMcpStatusAsync();
            if (mcp is { Ok: true, Mcp: { } status })
                State.SetMcp(status);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log($"Settings status refresh failed: {ex.Message}");
        }

        Update(State);
    }

    static bool DisconnectsMcp(McpStatusDto mcp) =>
        mcp.Status == SourceConnectionStatus.Connected || (mcp.Status == SourceConnectionStatus.Stale && !mcp.Available);

    async Task ToggleCursorAsync()
    {
        if (_busy)
            return;

        State.Sources.TryGetValue(CursorSource, out var current);
        var disconnect = current?.Status == SourceConnectionStatus.Connected;
        CursorError.IsVisible = false;
        await RunAsync(async () =>
        {
            var result = disconnect
                ? await Client.DisconnectSourceAsync(CursorSource)
                : await Client.ConnectSourceAsync(CursorSource);
            if (result is { Ok: true, Source: { } source })
            {
                State.SetSource(source);
                return;
            }

            Log($"Cursor {(disconnect ? "disconnect" : "connect")} failed: {result.Error}");
            CursorError.Text = "Не удалось изменить ~/.cursor/hooks.json: файл не разобрать или не записать. Исправьте его вручную и попробуйте снова.";
            CursorError.IsVisible = true;
        });
    }

    async Task ToggleMcpAsync()
    {
        if (_busy || State.Mcp is not { } current)
            return;

        var disconnect = DisconnectsMcp(current);
        McpError.IsVisible = false;
        await RunAsync(async () =>
        {
            var result = disconnect ? await Client.DisconnectMcpAsync() : await Client.ConnectMcpAsync();
            if (result is { Ok: true, Mcp: { } status })
            {
                State.SetMcp(status);
                McpHint.IsVisible = !disconnect && status.Status == SourceConnectionStatus.Connected;
                return;
            }

            Log($"MCP {(disconnect ? "disconnect" : "connect")} failed: {result.Error}");
            McpError.Text = current.Available
                ? "Не удалось изменить ~/.cursor/mcp.json: файл не разобрать или не записать. Исправьте его вручную и попробуйте снова."
                : "В этой сборке нет MCP-сервера.";
            McpError.IsVisible = true;
        });
    }

    async Task RunAsync(Func<Task> action)
    {
        _busy = true;
        Update(State);
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log($"Settings command failed: {ex}");
        }
        finally
        {
            _busy = false;
            Update(State);
        }
    }
}
