// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Synchronization.SnapSync;

/// <summary>Heals the mixed flat state left by snap sync by rebuilding its trie and replaying block access lists onto it.</summary>
/// <remarks>
/// Healing writes bypass the WAL, so they are not crash-durable until <see cref="FinalizeSync"/> flushes them, and
/// they are never rolled back - a failed run is discarded by the next flat snap initialization clearing the columns.
/// </remarks>
public interface IBalHealing
{
    /// <summary>Rebuilds the trie over the flat state left by snap sync and returns the root to heal from, or <c>null</c> on failure.</summary>
    /// <param name="updatedStorages">Hashed addresses of the accounts whose storage was synced after their account leaf.</param>
    Hash256? Reassemble(IReadOnlyCollection<Hash256> updatedStorages, CancellationToken token);

    /// <summary>Turns the state of block <paramref name="from"/>, rooted at <paramref name="baseRoot"/>, into the state of block <paramref name="to"/>; <c>null</c> if it could not.</summary>
    /// <param name="baseRoot">What <see cref="Reassemble"/> or the previous <see cref="ApplyRange"/> returned.</param>
    Hash256? ApplyRange(Hash256 baseRoot, BlockHeader from, BlockHeader to, CancellationToken token);

    /// <summary>Flushes the healed state and advances the persisted state pointer.</summary>
    /// <remarks>Without it the run is discarded by the next flat snap initialization.</remarks>
    void FinalizeSync(BlockHeader pivot);
}
