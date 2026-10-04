using System.Runtime.Versioning;
using static MachineVoice.Platform.MacOS.Interop.CoreFoundation;

namespace MachineVoice.Platform.MacOS;

/// <summary>
/// Main run loop for a host without NSApplication. AVSpeechSynthesizer delivers its delegate callbacks
/// on the main queue, so without a running main loop no progress or completion ever arrives.
/// </summary>
[SupportedOSPlatform("macos")]
public static class MacMainLoop
{
    public static bool IsMainThread => pthread_main_np() != 0;

    /// <summary>Blocks the main thread until <paramref name="cancellationToken"/> is cancelled.</summary>
    public static void Run(CancellationToken cancellationToken)
    {
        if (!IsMainThread)
            throw new InvalidOperationException("The main run loop can only run on the main thread.");

        var loop = CFRunLoopGetMain();
        using var registration = cancellationToken.Register(() => CFRunLoopStop(loop));
        while (!cancellationToken.IsCancellationRequested)
        {
            // With no sources attached the loop returns at once; sleep instead of spinning.
            if (CFRunLoopRunInMode(DefaultMode, 0.25, returnAfterSourceHandled: false) == RunLoopRunFinished)
                Thread.Sleep(10);
        }
    }
}
