using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MachineVoice.Stage0.Tts;

/// <summary>
/// Speaks through /usr/bin/say. Pause and resume suspend the process with SIGSTOP / SIGCONT,
/// which leaves CoreAudio replaying the last buffer: audible crackling while paused.
/// </summary>
internal sealed class SaySpeaker : ISpeaker
{
    private const int SIGSTOP = 17;
    private const int SIGCONT = 19;

    [DllImport("/usr/lib/libSystem.dylib", SetLastError = true)]
    private static extern int kill(int pid, int sig);

    private Process? _process;

    public string Name => $"say (pid {_process?.Id})";
    public SpeakerState State { get; private set; } = SpeakerState.Idle;

    public event Action<SpeakerState>? StateChanged;

    public void Speak(string text)
    {
        Stop();

        var process = new Process
        {
            StartInfo = new ProcessStartInfo("/usr/bin/say")
            {
                RedirectStandardInput = true,
                UseShellExecute = false,
            },
            EnableRaisingEvents = true,
        };
        process.Exited += (_, _) =>
        {
            if (ReferenceEquals(_process, process))
            {
                _process = null;
                SetState(SpeakerState.Idle);
            }
            process.Dispose();
        };

        process.Start();
        process.StandardInput.Write(text);
        process.StandardInput.Close();
        _process = process;
        SetState(SpeakerState.Speaking);
    }

    public void Pause()
    {
        if (State == SpeakerState.Speaking && Signal(SIGSTOP))
            SetState(SpeakerState.Paused);
    }

    public void Resume()
    {
        if (State == SpeakerState.Paused && Signal(SIGCONT))
            SetState(SpeakerState.Speaking);
    }

    public void Stop()
    {
        var process = _process;
        if (process is null)
            return;

        _process = null;
        try
        {
            if (!process.HasExited)
                process.Kill();
        }
        catch (InvalidOperationException)
        {
        }
        SetState(SpeakerState.Idle);
    }

    private bool Signal(int signal)
    {
        var process = _process;
        if (process is null || process.HasExited)
            return false;

        if (kill(process.Id, signal) == 0)
            return true;

        Log.Write($"kill({process.Id}, {signal}) не удался: errno {Marshal.GetLastPInvokeError()}");
        return false;
    }

    private void SetState(SpeakerState state)
    {
        if (State == state)
            return;
        State = state;
        StateChanged?.Invoke(state);
    }
}
