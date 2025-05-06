using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.State;
using Nethermind.TxPool.Collections;

namespace Nethermind.TxPool.Filters
{
    internal sealed class DelegatedAccountFilter(
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

            if ((!state.SenderAccount.HasCode || !codeInfoRepository.TryGetDelegation(worldState, state.SenderAccount.CodeHash, spec, out _))
                && !pendingDelegations.HasPending(tx.SenderAddress!))
                return AcceptTxResult.Accepted;
            //If the account is delegated or has pending delegation we only accept the next transaction nonce 
            if (state.SenderAccount.Nonce != tx.Nonce)
            {
                return AcceptTxResult.NotCurrentNonceForDelegation;
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
