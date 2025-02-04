using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.State;
using Nethermind.TxPool.Collections;

namespace Nethermind.TxPool.Filters;

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

        if (!codeInfoRepository.TryGetDelegation(worldState, tx.SenderAddress!, spec, out _))
            return AcceptTxResult.Accepted;
        // Transactions from the same source can only be either blob transactions or other type 
        if (tx.SupportsBlobs ? !blobPool.BucketEmptyExcept(tx.SenderAddress!, (t) => t.Nonce == tx.Nonce)
            : !standardPool.BucketEmptyExcept(tx.SenderAddress!, (t) => t.Nonce == tx.Nonce))
        {
            return AcceptTxResult.MoreThanOneTxPerDelegatedAccount;
        }
        return AcceptTxResult.Accepted;
    }
}
