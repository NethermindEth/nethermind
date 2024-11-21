using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.State;
using Nethermind.TxPool.Collections;

namespace Nethermind.TxPool.Filters
{
    internal sealed class OnlyOneTxPerDelegatedEOA(
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
            if (!spec.IsEip7702Enabled
                || !tx.HasAuthorizationList)
                return AcceptTxResult.Accepted;

            if (!codeInfoRepository.TryGetDelegation(worldState, tx.SenderAddress!, out _))
                return AcceptTxResult.Accepted;

            if (standardPool.ContainsBucket(tx.SenderAddress!) || blobPool.ContainsBucket(tx.SenderAddress!))
            {
                return AcceptTxResult.OnlyOneTxPerDelegatedAccount;
                                
            }
            return AcceptTxResult.Accepted;
        }
    }
}
