namespace Nethermind.Network.P2P.Subprotocols.Eth
{
    public class BlockBodiesMessage : P2PMessage
    {
        public override int PacketType { get; } = 5;
        public override int Protocol { get; } = 1;
    }
}