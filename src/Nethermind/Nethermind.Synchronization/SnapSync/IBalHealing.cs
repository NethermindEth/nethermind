// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Synchronization.SnapSync;

public interface IBalHealing
{
    /// <summary>Rebuilds the trie over the flat state left by snap sync and returns the root to heal from, or <c>null</c> on failure.</summary>
    /// <param name="updatedStorages">Hashed addresses of the accounts whose storage was synced after their account leaf.</param>
    Hash256? Reassemble(IReadOnlyCollection<Hash256> updatedStorages, CancellationToken token);

    /// <summary>Turns the state of block <paramref name="from"/>, rooted at <paramref name="baseRoot"/>, into the state of block <paramref name="to"/>; <c>null</c> if it could not.</summary>
    Hash256? ApplyRange(Hash256 baseRoot, BlockHeader from, BlockHeader to, CancellationToken token);

    /// <summary>Makes the healed state durable — healing writes bypass the WAL, so a run that ends without this leaves nothing behind.</summary>
    void FinalizeSync(BlockHeader pivot);
}
