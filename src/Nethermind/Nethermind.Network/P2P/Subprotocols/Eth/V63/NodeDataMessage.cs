namespace Nethermind.Network.P2P.Subprotocols.Eth.V63
{
    public class NodeDataMessage : P2PMessage
    {
        public override int PacketType { get; } = 0x0e;
        public override string Protocol { get; } = "eth";
    }
}