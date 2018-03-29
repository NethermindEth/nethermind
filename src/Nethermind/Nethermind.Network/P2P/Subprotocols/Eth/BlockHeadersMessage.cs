namespace Nethermind.Network.P2P.Subprotocols.Eth
{
    public class BlockHeadersMessage : P2PMessage
    {
        public override int PacketType { get; } = 4;
        public override int Protocol { get; } = 1;
    }
}