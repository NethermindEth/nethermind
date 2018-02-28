using Nethermind.Core.Crypto;

namespace Nethermind.Discovery.Messages
{
    public interface INodeIdResolver
    {
        PublicKey GetNodeId(byte[] signature, int recoveryId, byte[] messageType, byte[] data);
    }
}