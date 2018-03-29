using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Network.Rlpx;

namespace Nethermind.Network.P2P
{
    public abstract class SessionBase
    {
        protected IPacketSender PacketSender { get; }
        protected ILogger Logger { get; }
        protected IMessageSerializationService SerializationService { get; }

        protected SessionBase(IMessageSerializationService serializationService, IPacketSender packetSender, PublicKey remoteNodeId, ILogger logger)
        {
            SerializationService = serializationService;
            PacketSender = packetSender;
            Logger = logger;
            RemoteNodeId = remoteNodeId;
        }
        
        public PublicKey RemoteNodeId { get; }
        public int RemotePort { get; protected set; }

        protected T Deserialize<T>(byte[] data) where T : P2PMessage
        {
            return SerializationService.Deserialize<T>(data);
        }

        protected void Send<T>(T message) where T : P2PMessage
        {
            Packet packet = new Packet(message.Protocol, message.PacketType, SerializationService.Serialize(message));
            PacketSender.Enqueue(packet);   
        }
    }
}