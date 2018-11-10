using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Blockchain.TransactionPools
{
    public class PendingTransactionThresholdValidator : IPendingTransactionThresholdValidator
    {
        private readonly int _obsoletePendingTransactionInterval;
        private readonly int _removePendingTransactionInterval;

        public PendingTransactionThresholdValidator(int obsoletePendingTransactionInterval = 15,
            int removePendingTransactionInterval = 600)
        {
            _obsoletePendingTransactionInterval = obsoletePendingTransactionInterval;
            _removePendingTransactionInterval = removePendingTransactionInterval;
        }

        public bool IsObsolete(UInt256 currentTimestamp, UInt256 transactionTimestamp)
            => !IsTimeInRange(currentTimestamp, transactionTimestamp, _obsoletePendingTransactionInterval);

        public bool IsRemovable(UInt256 currentTimestamp, UInt256 transactionTimestamp)
            => !IsTimeInRange(currentTimestamp, transactionTimestamp, _removePendingTransactionInterval);

        private static bool IsTimeInRange(UInt256 currentTimestamp, UInt256 transactionTimestamp, int threshold)
            => (currentTimestamp - transactionTimestamp) <= threshold;
    }
}