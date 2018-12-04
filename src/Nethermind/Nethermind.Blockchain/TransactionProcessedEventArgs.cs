using System;
using Nethermind.Core;

namespace Nethermind.Blockchain
{
    public class TransactionProcessedEventArgs : EventArgs
    {
        public TransactionReceipt TransactionReceipt { get; }

        public TransactionProcessedEventArgs(TransactionReceipt transactionReceipt)
        {
            TransactionReceipt = transactionReceipt;
        }
    }
}