using Nevermind.Discovery.Messages;

namespace Nevermind.Discovery
{
    public interface IDiscoveryManager
    {
        void HandleIncomingMessage(Message message);
        void SendMessage(Message message);
    }
}