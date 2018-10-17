using System;
using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Blockchain
{
    public class TransactionReceiptsCreatedEventArgs : EventArgs
    {
        public TransactionReceipt[] Receipts;
        
        public TransactionReceiptsCreatedEventArgs(TransactionReceipt[] receipts)
        {
            Receipts = receipts;
        }
    }
}