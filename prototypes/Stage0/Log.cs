namespace MachineVoice.Stage0;

internal static class Log
{
    public static void Write(string message) =>
        Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
}
