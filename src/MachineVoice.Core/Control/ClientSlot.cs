using System.Threading.Channels;
using MachineVoice.Protocol;

namespace MachineVoice.Core;

sealed class ClientSlot
{
    public Channel<ServerMessage> Outbound { get; } = Channel.CreateUnbounded<ServerMessage>();
}
