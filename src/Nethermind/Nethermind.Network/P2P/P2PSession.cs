using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Network.Rlpx;

namespace Nethermind.Network.P2P
{
    public class P2PSession : ISession
    {
        private const int P2PVersion = 1;
        private readonly IMessageSender _messageSender;

        public P2PSession(IMessageSender messageSender, PublicKey localNodeId, int listenPort)
        {
            _messageSender = messageSender;
            LocalNodeId = localNodeId;
            ListenPort = listenPort;
        }

        public int ListenPort { get; }

        public PublicKey LocalNodeId { get; }

        public void InitInbound(HelloMessage hello)
        {
        }

        public void InitOutbound()
        {
            HelloMessage helloMessage = new HelloMessage
            {
                Capabilities = new Dictionary<Capability, int>
                {
                    {Capability.Eth, 0}
                },
                ClientId = ClientVersion.Description,
                NodeId = LocalNodeId,
                ListenPort = ListenPort,
                P2PVersion = P2PVersion
            };

            _messageSender.Enqueue(helloMessage);
        }

        public void HandlePing()
        {
            _messageSender.Enqueue(PongMessage.Instance);
        }

        public void Disconnect(DisconnectReason disconnectReason)
        {
            DisconnectMessage message = new DisconnectMessage(disconnectReason);
            _messageSender.Enqueue(message);
        }

        public void Close(DisconnectReason disconnectReason)
        {
        }

        public void HandlePong()
        {
        }

        public void Ping()
        {
            // TODO: timers
            _messageSender.Enqueue(PingMessage.Instance);
        }
    }
}