namespace MachineVoice.Core;

static class CursorHookScript
{
    public const string ResourceName = "MachineVoice.Core.Cursor.hook.sh";

    public static byte[] Content { get; } = Load();

    static byte[] Load()
    {
        var assembly = typeof(CursorHookScript).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            var names = string.Join(", ", assembly.GetManifestResourceNames());
            throw new InvalidOperationException($"Missing embedded {ResourceName}. Resources: {names}");
        }

        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
