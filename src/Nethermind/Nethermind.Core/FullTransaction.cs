namespace Nethermind.Core
{
    public class FullTransaction
    {
        public int Index { get; }
        public Transaction Transaction { get; }
        public TxReceipt Receipt { get; }

        public FullTransaction(int index, Transaction transaction, TxReceipt receipt)
        {
            Index = index;
            Transaction = transaction;
            Receipt = receipt;
        }
    }
}