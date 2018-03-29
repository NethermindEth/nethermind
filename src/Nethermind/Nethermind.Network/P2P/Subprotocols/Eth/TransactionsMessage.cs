namespace Nethermind.Network.P2P.Subprotocols.Eth
{
    public class TransactionsMessage : P2PMessage
    {
        public override int PacketType { get; } = 2;
        public override int Protocol { get; } = 1;
    }

    public class GetBlockHashesMessage : P2PMessage
    {
        public override int PacketType { get; } = 3;
        public override int Protocol { get; } = 1;
    }

    public class BlockHashesMessage : P2PMessage
    {
        public override int PacketType { get; } = 4;
        public override int Protocol { get; } = 1;
    }
}