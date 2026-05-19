// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Synchronization.FastSync;

/// <summary>
/// Replacement for post-snap state healing. Implementations reconstruct any missing trie spine
/// locally from the leaves snap sync committed and, in the long run, apply buffered EIP-7928
/// Block Access Lists to catch up from the snap pivot to head.
/// </summary>
/// <remarks>
/// Callers (e.g. <c>StateSyncRunner</c>) capture the pivot headers and updated-storages tracker
/// themselves and pass them in — implementations don't read mutable sync state. A storage
/// backend that cannot support the algorithm (e.g. the hash-keyed Patricia store) returns a
/// no-op implementation, so callers can wire this in unconditionally.
/// </remarks>
public interface IBalHealing
{
    /// <summary>
    /// Run the BAL-based healing flow.
    /// </summary>
    /// <param name="firstPivot">The pivot snap sync started downloading against — i.e. the
    /// block whose state root the reassembled trie is expected to match.</param>
    /// <param name="lastPivot">The pivot snap sync ended at — the block we want the final state
    /// to be at after replaying BALs forward from <paramref name="firstPivot"/>. When equal to
    /// <paramref name="firstPivot"/> there is no BAL bridge to apply.</param>
    /// <param name="updatedStorageAccounts">Hashed accounts whose storage trie was touched
    /// during snap (snap's <c>UpdatedStorages</c> tracker). Their storage tries are reassembled
    /// first and the resulting roots fed back into the state-trie leaves.</param>
    /// <returns><see langword="true"/> when sync is now complete at <paramref name="lastPivot"/>
    /// and the caller may skip traditional healing; <see langword="false"/> on any unsupported
    /// config, mismatch, or failure so the caller falls back to <c>RunStateSyncRounds</c>.</returns>
    Task<bool> Run(BlockHeader firstPivot, BlockHeader lastPivot, IReadOnlyCollection<Hash256> updatedStorageAccounts, CancellationToken token);
}

/// <summary>
/// No-op implementation returned when the storage backend cannot support BAL healing
/// (e.g. the legacy hash-keyed Patricia store). Always returns <see langword="false"/> so the
/// caller falls through to the existing healing path.
/// </summary>
public sealed class NoopBalHealing : IBalHealing
{
    public static readonly NoopBalHealing Instance = new();
    private NoopBalHealing() { }

    public Task<bool> Run(BlockHeader firstPivot, BlockHeader lastPivot, IReadOnlyCollection<Hash256> updatedStorageAccounts, CancellationToken token)
        => Task.FromResult(false);
}
