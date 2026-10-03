// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Evm.State;

namespace Nethermind.TxPool.Filters;

/// <summary>Checks recent-root commitments even when a frame prefix needs no EVM simulation.</summary>
internal sealed class RecentRootFilter(IChainHeadInfoProvider headInfo) : IIncomingTxFilter
{
    public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions handlingOptions)
        => IsValid(tx, headInfo.ReadOnlyStateProvider, headInfo.HeadSlotNumber)
            ? AcceptTxResult.Accepted : AcceptTxResult.FrameTxRecentRootUnmet;

    internal static bool IsValid(Transaction tx, IReadOnlyStateProvider state, ulong? headSlot)
        => !tx.SupportsFrames || tx.RecentRootReferences is not { Length: > 0 }
            || RecentRootReferences.Validate(state, tx.RecentRootReferences,
                headSlot is { } slot && slot < ulong.MaxValue ? slot + 1 : null);
}
