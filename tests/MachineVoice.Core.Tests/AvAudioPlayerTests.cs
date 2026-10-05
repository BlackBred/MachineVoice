using System.Runtime.Versioning;
using System.Text;
using MachineVoice.Platform.MacOS;

namespace MachineVoice.Core.Tests;

/// <summary>The real AVAudioPlayer at zero volume. It needs no main loop, so it runs in the test process.</summary>
[SupportedOSPlatform("macos")]
public class AvAudioPlayerTests
{
    [MacOSFact(Timeout = 20000)]
    public async Task Play_ReportsTheClip_UntilItEnds()
    {
        using var player = new AvAudioPlayer(volume: 0);
        player.Play(SilentWav(seconds: 0.4));

        var state = player.State;
        Assert.True(state.Active);
        Assert.InRange(state.Duration, 0.35, 0.45);

        var deadline = Environment.TickCount64 + 5000;
        while (player.State.Active && Environment.TickCount64 < deadline)
            await Task.Delay(20);
        Assert.False(player.State.Active);
    }

    [MacOSFact(Timeout = 20000)]
    public async Task Pause_HoldsThePosition_UntilResume()
    {
        using var player = new AvAudioPlayer(volume: 0);
        player.Play(SilentWav(seconds: 0.6));
        await Task.Delay(150);
        player.Pause();
        var pausedAt = player.State.Position;

        await Task.Delay(800);
        var paused = player.State;
        Assert.True(paused.Active);
        Assert.Equal(pausedAt, paused.Position, precision: 3);

        player.Resume();
        var deadline = Environment.TickCount64 + 5000;
        while (player.State.Active && Environment.TickCount64 < deadline)
            await Task.Delay(20);
        Assert.False(player.State.Active);
    }

    [MacOSFact(Timeout = 20000)]
    public async Task Rate_SpeedsUpTheClipThatPlays_WithoutResumingAPause()
    {
        using var player = new AvAudioPlayer(volume: 0);
        player.Play(SilentWav(seconds: 1.6));
        player.Pause();
        player.Rate = 2;
        var pausedAt = player.State.Position;
        await Task.Delay(300);
        Assert.Equal(pausedAt, player.State.Position, precision: 3);

        player.Resume();
        var started = Environment.TickCount64;
        while (player.State.Active && Environment.TickCount64 - started < 5000)
            await Task.Delay(10);
        var elapsed = Environment.TickCount64 - started;

        Assert.False(player.State.Active);
        Assert.InRange(elapsed, 500, 1300);
        Assert.Equal(2, player.Rate);
    }

    [MacOSFact(Timeout = 20000)]
    public async Task Seek_MovesAPausedClip_ThatPlaysFromThere()
    {
        using var player = new AvAudioPlayer(volume: 0);
        player.Play(SilentWav(seconds: 2));
        player.Pause();
        player.Seek(1.7);
        await Task.Delay(200);
        Assert.Equal(1.7, player.State.Position, precision: 2);
        Assert.True(player.State.Active);

        player.Resume();
        var started = Environment.TickCount64;
        while (player.State.Active && Environment.TickCount64 - started < 5000)
            await Task.Delay(10);
        Assert.False(player.State.Active);
        Assert.InRange(Environment.TickCount64 - started, 100, 900);
    }

    [MacOSFact]
    public void Play_RejectsDataThatIsNotAudio()
    {
        using var player = new AvAudioPlayer(volume: 0);
        Assert.Throws<InvalidDataException>(() => player.Play(Encoding.UTF8.GetBytes("{\"detail\":\"not audio\"}")));
        Assert.False(player.State.Active);
    }

    /// <summary>16-bit mono PCM at 24 kHz, the format mlx-audio returns for Qwen3-TTS.</summary>
    static byte[] SilentWav(double seconds)
    {
        const int rate = 24000;
        var dataSize = (int)(rate * seconds) * 2;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataSize);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(rate);
        writer.Write(rate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataSize);
        writer.Write(new byte[dataSize]);
        writer.Flush();
        return stream.ToArray();
    }
}
