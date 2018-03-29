using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Network.Rlpx;

namespace Nethermind.Network.P2P.Subprotocols.Eth
{
    public class Eth63Session : Eth62Session
    {
        public Eth63Session(
            IMessageSerializationService serializer,
            IPacketSender packetSender,
            ILogger logger,
            PublicKey remoteNodeId,
            int remotePort) : base(serializer, packetSender, logger, remoteNodeId, remotePort)
        {
            RemotePort = remotePort;
        }

        public override int MessageIdSpaceSize => base.MessageIdSpaceSize + 4;

        public override void HandleMessage(Packet packet)
        {
            base.HandleMessage(packet);
            throw new NotImplementedException();
        }
    }
}