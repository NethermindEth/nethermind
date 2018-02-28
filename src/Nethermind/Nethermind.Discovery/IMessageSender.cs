using Nethermind.Discovery.Messages;

namespace Nethermind.Discovery
{
    public interface IMessageSender
    {
        void SendMessage(DiscoveryMessage discoveryMessage);
    }
}