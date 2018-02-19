using Nevermind.Core.Crypto;

namespace Nevermind.Discovery.Messages
{
    public interface INodeIdResolver
    {
        PublicKey GetNodeId(byte[] signature, byte[] messageType, byte[] data);
    }
}