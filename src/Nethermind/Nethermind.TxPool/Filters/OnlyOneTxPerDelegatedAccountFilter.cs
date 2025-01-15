using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.State;
using Nethermind.TxPool.Collections;
using System.Collections.Concurrent;

namespace Nethermind.TxPool.Filters
{
    internal sealed class OnlyOneTxPerDelegatedAccountFilter(
        IChainHeadSpecProvider specProvider,
        TxDistinctSortedPool standardPool,
        TxDistinctSortedPool blobPool,
        IReadOnlyStateProvider worldState,
        ICodeInfoRepository codeInfoRepository,
        DelegationCache pendingDelegations) : IIncomingTxFilter
    {
        public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions txHandlingOptions)
        {
            IReleaseSpec spec = specProvider.GetCurrentHeadSpec();
            if (!spec.IsEip7702Enabled)
                return AcceptTxResult.Accepted;

            if (pendingDelegations.HasPending(tx.SenderAddress!, tx.Nonce))
                return AcceptTxResult.PendingDelegation;

            if (!codeInfoRepository.TryGetDelegation(worldState, tx.SenderAddress!, out _))
                return AcceptTxResult.Accepted;
            Transaction[] currentTxs;

            //Transactios from the same source can only be either blob transactions or some other type 
            if (standardPool.TryGetBucket(tx.SenderAddress!, out currentTxs) || blobPool.TryGetBucket(tx.SenderAddress!, out currentTxs))
            {
                foreach (Transaction existingTx in currentTxs)
                {
                    if (existingTx.Nonce == tx.Nonce)
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
