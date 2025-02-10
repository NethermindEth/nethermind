using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.State;
using Nethermind.TxPool.Collections;
using System.Collections.Concurrent;
using System.Linq;

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

            if (tx.HasAuthorizationList && AuthorityHasPendingTx(tx.AuthorizationList))
                return AcceptTxResult.DelegatorHasPendingTx;

            if (pendingDelegations.HasPending(tx.SenderAddress!, tx.Nonce))
            {
                //Check if the sender has a self-sponsored SetCode transaction with same nonce.
                //If he does then this is a replacement tx and should be accepted
                if (!standardPool.BucketAny(tx.SenderAddress!,
                    t => t.Nonce == tx.Nonce
                    && t.HasAuthorizationList
                    && t.AuthorizationList.Any(tuple => tuple.Authority == tx.SenderAddress)))
                {
                    return AcceptTxResult.PendingDelegation;
                }
            }

            if (!codeInfoRepository.TryGetDelegation(worldState, tx.SenderAddress!, out _))
                return AcceptTxResult.Accepted;
            //If the account is delegated we only accept the next transaction nonce 
            if (state.SenderAccount.Nonce != tx.Nonce)
            {
                return AcceptTxResult.OnlyExactNonceForDelegatedAccount;
            }
            return AcceptTxResult.Accepted;
        }

        private bool AuthorityHasPendingTx(AuthorizationTuple[] authorizations)
        {
            foreach (AuthorizationTuple authorization in authorizations)
            {
                //RecoverAuthorityFilter runs before this, so if a signature is null, we assume it is bad
                if (authorization.Authority is null)
                {
                    continue;
                }
                if (standardPool.ContainsBucket(authorization.Authority!)
                    || blobPool.ContainsBucket(authorization.Authority!))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
