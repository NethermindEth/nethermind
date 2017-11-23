using Nevermind.Core.Crypto;

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

        public long GasUsed { get; set; }
        public Bloom Bloom { get; set; }
        public LogEntry[] Logs { get; set; }
    }
}