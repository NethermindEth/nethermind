// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
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
        private readonly IAccountStateProvider _stateProvider;

        public DeployedCodeFilter(IChainHeadSpecProvider specProvider, IAccountStateProvider stateProvider)
        {
            _specProvider = specProvider;
            _stateProvider = stateProvider;
        }
        public AcceptTxResult Accept(Transaction tx, TxFilteringState state, TxHandlingOptions txHandlingOptions) =>
            _stateProvider.IsInvalidContractSender(_specProvider.GetCurrentHeadSpec(), tx.SenderAddress!)
                ? AcceptTxResult.SenderIsContract
                : AcceptTxResult.Accepted;
    }
}
