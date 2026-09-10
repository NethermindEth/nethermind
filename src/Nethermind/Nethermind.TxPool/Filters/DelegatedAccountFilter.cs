using Nethermind.Core;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.TxPool.Collections;

namespace Nethermind.TxPool.Filters
{
    internal sealed class DelegatedAccountFilter(
        TxDistinctSortedPool standardPool,
        TxDistinctSortedPool blobPool,
        IReadOnlyStateProvider worldState,
        DelegationCache pendingDelegations) : IIncomingTxFilter
    {
        public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions txHandlingOptions)
        {
            if (!state.HeadSpec.IsEip7702Enabled)
                return AcceptTxResult.Accepted;

            if (tx.HasAuthorizationList && AuthorityHasPendingTx(tx.AuthorizationList))
                return AcceptTxResult.DelegatorHasPendingTx;

            if ((!state.SenderAccount.HasCode || !worldState.IsDelegatedCode(state.SenderAccount.CodeHash))
                && !pendingDelegations.HasPending(tx.SenderAddress!))
                return AcceptTxResult.Accepted;
            // Every fresh EIP-8250 key is current at sequence 0, so the count bound the account-nonce gate gave
            // this sender is taken directly: one authorization must not invalidate a bucketful at once.
            if (KeyedNonceManager.UsesKeyedNonce(tx))
            {
                return SenderHasOtherPendingTx(tx)
                    ? AcceptTxResult.NotCurrentNonceForDelegation.WithMessage("delegated sender already has a pending transaction")
                    : AcceptTxResult.Accepted;
            }

            //If the account is delegated or has pending delegation we only accept the next transaction nonce
            if (state.SenderAccount.Nonce != tx.Nonce)
            {
                return AcceptTxResult.NotCurrentNonceForDelegation;
            }
            return AcceptTxResult.Accepted;
        }

        /// <summary>Whether the sender already has a pending transaction <paramref name="tx"/> would not displace.</summary>
        private bool SenderHasOtherPendingTx(Transaction tx) =>
            (standardPool.ContainsBucket(tx.SenderAddress!) || blobPool.ContainsBucket(tx.SenderAddress!))
            && PendingReplacement.Find(tx, standardPool, blobPool) is null;

        private bool AuthorityHasPendingTx(AuthorizationTuple[] authorizations)
        {
            foreach (AuthorizationTuple authorization in authorizations)
            {
                //RecoverAuthorityFilter runs before this, so if a signature is null, we assume it is bad
                if (authorization.Authority is null)
                {
                    continue;
                }
                if (standardPool.ContainsBucket(authorization.Authority)
                    || blobPool.ContainsBucket(authorization.Authority))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
