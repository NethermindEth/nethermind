// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;

namespace Nethermind.TxPool.Filters
{
    /// <summary>
    /// Filters out transactions that sender has any code deployed. If <see cref="IReleaseSpec.IsEip3607Enabled"/> is enabled.
    /// EIP-8141 frame transactions are exempt.
    /// </summary>
    internal sealed class DeployedCodeFilter(IReadOnlyStateProvider worldState, IChainHeadSpecProvider specProvider) : IIncomingTxFilter
    {
        private readonly Func<Address, bool> _isDelegatedCode = worldState.IsDelegatedCode;
        public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions txHandlingOptions)
        {
            // EIP-8141 §Mempool exempts frame transactions from EIP-3607: one is authorised by the
            // sender's own code running in the validation prefix, the very case EIP-3607 forbids elsewhere.
            if (tx.Type == TxType.FrameTx)
            {
                return AcceptTxResult.Accepted;
            }

            IReleaseSpec spec = specProvider.GetCurrentHeadSpec();
            return state.SenderAccount.HasCode && worldState.IsInvalidContractSender(spec,
                tx.SenderAddress!,
                _isDelegatedCode)
                ? AcceptTxResult.SenderIsContract
                : AcceptTxResult.Accepted;
        }
    }
}
