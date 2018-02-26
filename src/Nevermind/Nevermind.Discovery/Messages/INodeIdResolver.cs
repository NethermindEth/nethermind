using Nevermind.Core.Crypto;

namespace Nevermind.Discovery.Messages
{
    public interface INodeIdResolver
    {
        PublicKey GetNodeId(byte[] signature, int recoveryId, byte[] messageType, byte[] data);
    }
}