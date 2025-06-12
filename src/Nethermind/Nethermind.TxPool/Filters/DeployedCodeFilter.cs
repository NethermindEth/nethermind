// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.State;

namespace Nethermind.TxPool.Filters
{
    /// <summary>
    /// Filters out transactions that sender has any code deployed. If <see cref="IReleaseSpec.IsEip3607Enabled"/> is enabled.
    /// </summary>
    internal sealed class DeployedCodeFilter(IReadOnlyStateProvider worldState, ICodeInfoRepository codeInfoRepository, IChainHeadSpecProvider specProvider) : IIncomingTxFilter
    {
        private readonly Func<Address, bool> _isDelegatedCode = (sender) => codeInfoRepository.TryGetDelegation(worldState, sender, specProvider.GetCurrentHeadSpec(), out _);
        public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions txHandlingOptions)
        {
            IReleaseSpec spec = specProvider.GetCurrentHeadSpec();
            return state.SenderAccount.HasCode && worldState.IsInvalidContractSender(spec,
                tx.SenderAddress!,
                _isDelegatedCode)
                ? AcceptTxResult.SenderIsContract
                : AcceptTxResult.Accepted;
        }
    }
}
