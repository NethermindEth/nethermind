using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Blockchain.TransactionPools
{
    public interface IPendingTransactionThresholdValidator
    {
        bool IsObsolete(UInt256 currentTimestamp, UInt256 transactionTimestamp);
        bool IsRemovable(UInt256 currentTimestamp, UInt256 transactionTimestamp);
    }
}