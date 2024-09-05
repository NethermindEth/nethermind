// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.State;

namespace Nethermind.TxPool.Filters
{
    /// <summary>
    /// Filters out transactions that sender has any code deployed. If <see cref="IReleaseSpec.IsEip3607Enabled"/> is enabled.
    /// </summary>
    internal sealed class DeployedCodeFilter(IWorldState worldState, ICodeInfoRepository codeInfoRepository, IChainHeadSpecProvider specProvider) : IIncomingTxFilter
    {
        public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions txHandlingOptions)
        {
            return specProvider.GetCurrentHeadSpec().IsEip3607Enabled && state.SenderAccount.HasCode && (!specProvider.GetCurrentHeadSpec().IsEip7702Enabled || !codeInfoRepository.IsDelegation(worldState, tx.SenderAddress!, out _))
                ? AcceptTxResult.SenderIsContract
                : AcceptTxResult.Accepted;
        }
    }
}
