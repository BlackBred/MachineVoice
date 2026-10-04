namespace MachineVoice.Core;

static class DurableFile
{
    public static void Write(string path, ReadOnlySpan<byte> data)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var tmp = path + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.Write(data);
            stream.Flush(flushToDisk: true);
        }

        AppLayout.SetPrivateFile(tmp);
        File.Move(tmp, path, overwrite: true);
    }
}
