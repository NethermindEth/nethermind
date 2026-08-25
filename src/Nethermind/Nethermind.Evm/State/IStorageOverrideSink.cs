// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Evm.State;

/// <summary>
/// Accepts per-account storage overrides that are resolved on the first read of each slot instead of being
/// written into the state up front.
/// </summary>
/// <remarks>
/// A slot the call never touches costs nothing; one that is read is materialized into the block-level change
/// set at that read, so <see cref="IWorldState.GetOriginal"/> and SSTORE metering see the override exactly as
/// after an eager write followed by a commit. The overrides live for the current scope only and are never
/// merkleized: a caller that needs them reflected in a state root must write them eagerly.
/// </remarks>
public interface IStorageOverrideSink
{
    /// <summary>Installs the storage overrides of <paramref name="address"/> for the current scope.</summary>
    /// <param name="address">The overridden account.</param>
    /// <param name="slots">Slot values keyed by slot index; the dictionary is read, never copied or modified.</param>
    /// <param name="replaceAll">
    /// <see langword="true"/> for a full <c>state</c> override, where a slot missing from <paramref name="slots"/> reads
    /// as zero without consulting the backing store; <see langword="false"/> for a <c>stateDiff</c>, where it falls
    /// through to the underlying state.
    /// </param>
    /// <returns><see langword="false"/> when overrides cannot be served lazily here and must be written eagerly instead.</returns>
    bool TrySetStorageOverrides(Address address, Dictionary<UInt256, ValueHash256> slots, bool replaceAll);
}
