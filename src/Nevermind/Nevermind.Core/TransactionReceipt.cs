using System.Numerics;
using Nevermind.Core.Encoding;

namespace Nevermind.Core
{
    public class TransactionReceipt
    {
        public Keccak PostTransactionState { get; set; }
        public BigInteger GasUsed { get; set; }
        public Bloom Bloom { get; set; }
        public LogEntry[] Logs { get; set; }
    }
}