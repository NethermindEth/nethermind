namespace Nethermind.Network.P2P.Subprotocols.Eth.V63
{
    public class GetReceiptsMessage : P2PMessage
    {
        public override int PacketType { get; } = 0x0f;
        public override string Protocol { get; } = "eth";
    }
}