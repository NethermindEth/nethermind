// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;

namespace Nethermind.Consensus.Processing.ParallelProcessing.BlockStm;

/// <summary>
/// Per-tx <see cref="IFeeRecorder"/> that buckets gas-beneficiary and fee-collector credits
/// into the block-level <see cref="FeeAccumulator"/> (breaking the W-A-W dep all txs would
/// otherwise create on those addresses) and falls back to a direct <see cref="IWorldState"/>
/// credit for any other recipient — Optimism's L1FeeReceiver / OperatorFeeRecipient and any
/// future protocol-defined fee target. The fallback re-introduces a W-A-W dep on the
/// recipient (correct semantics for an arbitrary address); without it the credit would
/// silently vanish if it ever reached this path.
/// </summary>
internal sealed class ParallelFeeRecorder(
    int txIndex,
    FeeAccumulator feeAccumulator,
    MultiVersionMemoryScopeProvider scopeProvider,
    IWorldState worldState,
    IReleaseSpec spec) : IFeeRecorder
{
    public void RecordFee(Address recipient, in UInt256 amount, bool createAccount)
    {
        FeeRecipientKind kind = feeAccumulator.GetFeeKind(recipient);
        if (kind != FeeRecipientKind.None)
        {
            feeAccumulator.RecordFee(txIndex, recipient, in amount, createAccount);
            ParallelStateKey key = ParallelStateKey.ForFee(kind, txIndex);
            ref object value = ref CollectionsMarshal.GetValueRefOrAddDefault(scopeProvider.WriteSet, key, out bool exists);
            value = exists && value is UInt256 existingAmount ? existingAmount + amount : amount;
            return;
        }

        // Unknown recipient. Skip the no-op case; otherwise write to the per-tx WorldState
        // directly — the credit flows through intra-block cache and is captured into the
        // per-tx WriteSet on commit, same as any ordinary account update.
        if (amount.IsZero && !createAccount) return;
        worldState.AddToBalanceAndCreateIfNotExists(recipient, amount, spec);
    }
}
