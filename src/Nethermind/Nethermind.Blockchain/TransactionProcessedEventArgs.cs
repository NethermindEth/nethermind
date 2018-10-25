using System;
using Nethermind.Core;

namespace Nethermind.Blockchain
{
    public class TransactionProcessedEventArgs : EventArgs
    {
        public TransactionReceipt Receipt { get; }

        public TransactionProcessedEventArgs(TransactionReceipt receipt)
        {
            Receipt = receipt;
        }
    }
}