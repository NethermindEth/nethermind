// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
    /// single-property contract still loads: it keeps reporting no slice, and callers that check
    /// <see cref="GeneratedBlockAccessList"/> fall back to offering no diff at all.</remarks>
    void SetGeneratingBlockAccessList(BlockAccessListAtIndex? bal) { }
}
