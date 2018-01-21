using System;
using Nevermind.Discovery.Messages;

namespace Nevermind.Discovery
{
    public class DiscoveryManager : IDiscoveryManager
    {
        public void HandleIncomingMessage(Message message)
        {

        }

        public void SendMessage(Message message)
        {
            throw new NotImplementedException();
        }
    }
}