using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Encoding;

namespace Nethermind.Network.P2P.Subprotocols.Eth
{
    public class TransactionsMessageSerializer : IMessageSerializer<TransactionsMessage>
    {
        public byte[] Serialize(TransactionsMessage message, IMessagePad pad = null)
        {
            return Rlp.Encode(
                message.Transactions.Select(Rlp.Encode).ToArray()
            ).Bytes;
        }

        public TransactionsMessage Deserialize(byte[] bytes)
        {
            DecodedRlp decodedRlp = Rlp.Decode(new Rlp(bytes));
            Transaction[] transactions = new Transaction[decodedRlp.Length];
            for (int i = 0; i < decodedRlp.Length; i++)
            {
                DecodedRlp transactionRlp = decodedRlp.GetSequence(i);
                transactions[i] = Rlp.Decode<Transaction>(transactionRlp);
            }
            
            return new TransactionsMessage(transactions);
        }
    }
}