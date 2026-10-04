using System.Net.Sockets;
using System.Text.Json;
using MachineVoice.Protocol;

namespace MachineVoice.Core;

public static class IngestClient
{
    public static async Task<IngestResponse> SubmitAsync(
        string socketPath,
        SpeechDraft draft,
        CancellationToken cancellationToken = default)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken).ConfigureAwait(false);
        using var stream = new NetworkStream(socket, ownsSocket: false);
        var submit = new StoredSubmit
        {
            Version = ProtocolVersion.Current,
            Type = "submit",
            Source = draft.Source,
            Project = draft.Project,
            ConversationId = draft.ConversationId,
            GenerationId = draft.GenerationId,
            Topic = draft.Topic,
            Text = draft.Text,
        };
        var json = JsonSerializer.Serialize(submit, IngestJsonContext.Default.StoredSubmit);
        await Ndjson.WriteLineAsync(stream, json, cancellationToken).ConfigureAwait(false);

        using var reader = Ndjson.CreateReader(stream);
        var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(line))
            throw new IOException("Ingest connection closed before a response.");

        return JsonSerializer.Deserialize(line, IngestJsonContext.Default.IngestResponse)
            ?? throw new InvalidDataException("Empty ingest response.");
    }
}
