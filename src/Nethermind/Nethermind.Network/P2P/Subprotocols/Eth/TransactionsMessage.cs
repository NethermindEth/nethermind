using Nethermind.Core;

namespace Nethermind.Network.P2P.Subprotocols.Eth
{
    public class TransactionsMessage : P2PMessage
    {
        public Transaction[] Transactions { get; }
        public override int PacketType { get; } = 2;
        public override int Protocol { get; } = 1;

        public TransactionsMessage()
        {
        }

        public TransactionsMessage(params Transaction[] transactions)
        {
            Transactions = transactions;
        }
    }
}