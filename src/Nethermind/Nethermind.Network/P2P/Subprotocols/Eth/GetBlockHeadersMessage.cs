namespace Nethermind.Network.P2P.Subprotocols.Eth
{
    public class GetBlockHeadersMessage : P2PMessage
    {
        public override int PacketType { get; } = 3;
        public override int Protocol { get; } = 1;
    }
}