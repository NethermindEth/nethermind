// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

using Nethermind.Core.BlockAccessLists;

namespace Nethermind.Evm.State;

/// <summary>
/// Surfaces the per-tx BAL slice currently being recorded so transaction processing
/// can read in-flight changes (e.g. EIP-8037 self-destruct refund accounting).
/// </summary>
public interface IBlockAccessListSource
{
    BlockAccessListAtIndex? GeneratedBlockAccessList { get; }

    /// <summary>Starts recording into <paramref name="bal"/>, or stops recording when it is null.</summary>
    /// <remarks>Block processing installs one per block; simulation installs one per transaction that
    /// reads its own diff, and stays idle otherwise. Defaulted so a source written against the
    /// single-property contract still loads, but the default throws rather than silently no-opping:
    /// this hook drives the EIP-7906 POST_TX diff, so a source that reaches recording without
    /// supplying it is a wiring gap that must fail loud, not quietly disable a consensus behaviour.</remarks>
    void SetGeneratingBlockAccessList(BlockAccessListAtIndex? bal)
        => throw new NotSupportedException($"{GetType().Name} does not implement {nameof(SetGeneratingBlockAccessList)}; a source that records the EIP-7906 POST_TX diff must override it.");
}
