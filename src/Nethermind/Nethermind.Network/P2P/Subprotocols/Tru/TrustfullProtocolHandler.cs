using System;
using Nethermind.Core.Logging;
using Nethermind.Network.Rlpx;
using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P.Subprotocols.Tru
{
    public class TrustfullProtocolHandler : ProtocolHandlerBase, IProtocolHandler
    {
        public TrustfullProtocolHandler(IP2PSession p2PSession, IMessageSerializationService serializer, ILogManager logManager)
            : base(p2PSession, serializer, logManager)
        {
        }

        protected override TimeSpan InitTimeout => TimeSpan.FromSeconds(3);
        public byte ProtocolVersion => 1;
        public string ProtocolCode => "tru";
        public int MessageIdSpaceSize => 1;
        
        public void Init()
        {
        }

        public void HandleMessage(Packet message)
        {
            throw new NotImplementedException();
        }

        public void Close()
        {
            throw new NotImplementedException();
        }

        public void Disconnect(DisconnectReason disconnectReason)
        {
            throw new NotImplementedException();
        }

        public event EventHandler<ProtocolInitializedEventArgs> ProtocolInitialized;
        public event EventHandler<ProtocolEventArgs> SubprotocolRequested;
    }
}