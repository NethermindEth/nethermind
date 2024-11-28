using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.State;
using Nethermind.TxPool.Collections;

namespace Nethermind.TxPool.Filters
{
    internal sealed class OnlyOneTxPerDelegatedAccountFilter(
        IChainHeadSpecProvider specProvider,
        TxDistinctSortedPool standardPool,
        TxDistinctSortedPool blobPool,
        IReadOnlyStateProvider worldState,
        ICodeInfoRepository codeInfoRepository
        ) : IIncomingTxFilter
    {
        public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions txHandlingOptions)
        {
            IReleaseSpec spec = specProvider.GetCurrentHeadSpec();
            if (!spec.IsEip7702Enabled)
                return AcceptTxResult.Accepted;

            if (!codeInfoRepository.TryGetDelegation(worldState, tx.SenderAddress!, out _))
                return AcceptTxResult.Accepted;
            Transaction[] currentTx;
            if (standardPool.TryGetBucket(tx.SenderAddress!, out currentTx) || blobPool.TryGetBucket(tx.SenderAddress!, out currentTx))
            {
                foreach (Transaction t in currentTx)
                {
                    if (t.Nonce == tx.Nonce)
                    {
                        //This is a replacement tx so accept it, and let the comparers check for correct replacement rules
                        return AcceptTxResult.Accepted;
                    }
                }
                return AcceptTxResult.OnlyOneTxPerDelegatedAccount;
            }
            return AcceptTxResult.Accepted;
        }
    }
}
