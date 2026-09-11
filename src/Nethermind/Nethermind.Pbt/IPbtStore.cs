// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

/// <summary>Backing store used by <see cref="TrieUpdater"/> for canonical complete-key mutations.</summary>
public interface IPbtStore
{
    /// <summary>Gets a caller-owned lease for the complete group identified by <paramref name="groupKey"/>.</summary>
    /// <remarks>
    /// A non-null result transfers exactly one reference to the caller, which must invoke
    /// <see cref="IDisposable.Dispose"/> exactly once. Consumers must treat the payload as immutable.
    /// The reference keeps the payload valid until released, independently of the store. A missing group
    /// returns <see langword="null"/>. Implementations validate four-level boundary keys even on misses.
    /// The expected <paramref name="groupHash"/> identifies the logical subtree anchored at the group boundary,
    /// including its boundary node even when that node is physically stored in an ancestor group.
    /// It is the tree root for the root group and zero for an empty subtree; caches use it with the path.
    /// </remarks>
    RefCountingMemory? GetNodeGroup<TPath>(TPath groupKey, in ValueHash256 groupHash) where TPath : struct, IPbtNodePath<TPath>;

    /// <summary>Replaces or deletes the complete group identified by <paramref name="groupKey"/>.</summary>
    /// <remarks>
    /// A null payload deletes the group. A non-null payload is borrowed for the call; the caller retains
    /// its reference and must release it. Implementations retaining the immutable payload acquire their
    /// own reference before replacing the previous payload. Invalid keys or payloads leave the group unchanged.
    /// The new <paramref name="groupHash"/> uses the same boundary-anchored identity as reads, not the old hash.
    /// A null payload can delete an empty physical group while its logical subtree survives compression.
    /// </remarks>
    void SetNodeGroup<TPath>(TPath groupKey, in ValueHash256 groupHash, RefCountingMemory? payload) where TPath : struct, IPbtNodePath<TPath>;
}
