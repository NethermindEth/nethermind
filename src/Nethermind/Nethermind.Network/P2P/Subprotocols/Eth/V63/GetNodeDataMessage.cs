namespace Nethermind.Network.P2P.Subprotocols.Eth.V63
{
    public class GetNodeDataMessage : P2PMessage
    {
        public override int PacketType { get; } = 0x0d;
        public override string Protocol { get; } = "eth";
    }
}