// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.TxPool.Filters
{
    /// <summary>
    /// Filters out transactions that sender has any code deployed. If <see cref="IReleaseSpec.IsEip3607Enabled"/> is enabled.
    /// </summary>
    internal sealed class DeployedCodeFilter : IIncomingTxFilter
    {
        private readonly IChainHeadSpecProvider _specProvider;

        public DeployedCodeFilter(IChainHeadSpecProvider specProvider)
        {
            _specProvider = specProvider;
        }
        public AcceptTxResult Accept(Transaction tx, TxFilteringState state, TxHandlingOptions txHandlingOptions)
        {
            return _specProvider.GetCurrentHeadSpec().IsEip3607Enabled && state.SenderAccount.HasCode
                ? AcceptTxResult.SenderIsContract
                : AcceptTxResult.Accepted;
        }
    }
}
