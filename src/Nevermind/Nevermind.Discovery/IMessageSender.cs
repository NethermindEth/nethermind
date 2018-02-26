using Nevermind.Discovery.Messages;

namespace Nevermind.Discovery
{
    public interface IMessageSender
    {
        void SendMessage(DiscoveryMessage discoveryMessage);
    }
}