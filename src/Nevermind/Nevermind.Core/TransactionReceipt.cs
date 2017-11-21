using System.Numerics;
using Nevermind.Core.Encoding;

namespace Nevermind.Core
{
    public class TransactionReceipt
    {
        /// <summary>
        ///     EIP-658
        /// </summary>
        public byte StatusCode { get; set; }

        /// <summary>
        ///     Removed in EIP-658
        /// </summary>
        public Keccak PostTransactionState { get; set; }

        public BigInteger GasUsed { get; set; }
        public Bloom Bloom { get; set; }
        public LogEntry[] Logs { get; set; }
    }
}