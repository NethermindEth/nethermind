using Nethermind.Core.Crypto;
using Nethermind.Core.Model;

namespace Nethermind.Network.Discovery.Messages
{
    public interface INodeIdResolver
    {
        NodeId GetNodeId(byte[] signature, int recoveryId, byte[] messageType, byte[] data);
    }
}