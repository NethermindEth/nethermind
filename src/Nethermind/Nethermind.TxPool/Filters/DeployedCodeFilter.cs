// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Evm.State;

namespace Nethermind.TxPool.Filters
{
    /// <summary>
    /// Filters out transactions that sender has any code deployed. If <see cref="IReleaseSpec.IsEip3607Enabled"/> is enabled.
    /// </summary>
    internal sealed class DeployedCodeFilter(IReadOnlyStateProvider worldState) : IIncomingTxFilter
    {
        private readonly Func<Address, bool> _isDelegatedCode = worldState.IsDelegatedCode;
        public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions txHandlingOptions) =>
            state.SenderAccount.HasCode && worldState.IsInvalidContractSender(state.HeadSpec,
                tx.SenderAddress!,
                _isDelegatedCode)
                ? AcceptTxResult.SenderIsContract
                : AcceptTxResult.Accepted;
    }
}
