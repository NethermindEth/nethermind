using System;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Blockchain.TransactionPools
{
    public class TransactionPoolTimer : ITransactionPoolTimer
    {
        public UInt256 CurrentTimestamp => new UInt256(new DateTimeOffset(DateTime.UtcNow).ToUnixTimeSeconds());
    }
}