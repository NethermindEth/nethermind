using Nevermind.Core.Crypto;

namespace Nevermind.Discovery.Messages
{
    public interface INodeIdResolver
    {
        PublicKey GetNodeId(Message message);
    }
}