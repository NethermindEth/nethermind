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
                Transaction[] userTxs = standardPool.GetSnapshot();
                //Check if the sender has a self-sponsored SetCode transaction with same nonce.
                //If he does then this is a replacement tx and should be accepted
                if (userTxs.Any(t => t.Nonce == tx.Nonce
                && t.HasAuthorizationList
                && t.AuthorizationList.Any(tuple => tuple.Authority == tx.SenderAddress)))
                {
                    return AcceptTxResult.Accepted;
                }
                return AcceptTxResult.PendingDelegation;
            }

            if (!codeInfoRepository.TryGetDelegation(worldState, tx.SenderAddress!, out _))
                return AcceptTxResult.Accepted;
            //Transactions from the same source can only be either blob transactions or other type 
            if (tx.SupportsBlobs ? !blobPool.BucketEmptyExcept(tx.SenderAddress!, (t) => t.Nonce == tx.Nonce)
                : !standardPool.BucketEmptyExcept(tx.SenderAddress!, (t) => t.Nonce == tx.Nonce))
            {
                return AcceptTxResult.MoreThanOneTxPerDelegatedAccount;
            }
            return AcceptTxResult.Accepted;
        }

        private bool AuthorityHasPendingTx(AuthorizationTuple[] authorizations)
        {
            foreach (AuthorizationTuple authorization in authorizations)
            {
                //RecoverAuthorityFilter runs before so if a signature is null, it must be bad
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
