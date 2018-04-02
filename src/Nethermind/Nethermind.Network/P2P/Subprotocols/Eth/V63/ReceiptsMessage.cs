namespace Nethermind.Network.P2P.Subprotocols.Eth.V63
{
    public class ReceiptsMessage : P2PMessage
    {
        public override int PacketType { get; } = 0x10;
        public override string Protocol { get; } = "eth";
    }
}