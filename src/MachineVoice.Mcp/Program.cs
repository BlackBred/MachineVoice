using System.Text;
using MachineVoice.Mcp;
using MachineVoice.Protocol;

string socketPath;
switch (args)
{
    case []:
        socketPath = McpServer.DefaultSocketPath;
        break;
    case ["--socket", var path]:
        socketPath = path;
        break;
    default:
        Console.Error.WriteLine("Usage: MachineVoice.Mcp [--socket <path to control.sock>]");
        return 2;
}

// stdout carries the MCP messages, so diagnostics go to stderr.
var server = new McpServer(
    async cancellationToken => await SocketControlClient.ConnectAsync(socketPath, cancellationToken).ConfigureAwait(false),
    message => Console.Error.WriteLine(message));
var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
using var input = new StreamReader(Console.OpenStandardInput(), utf8);
await using var output = new StreamWriter(Console.OpenStandardOutput(), utf8);
await server.RunAsync(input, output);
return 0;
