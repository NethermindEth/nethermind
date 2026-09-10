// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Buffers;

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
    /// </remarks>
    RefCountingMemory? GetNodeGroup<TPath>(TPath groupKey) where TPath : struct, IPbtNodePath<TPath>;

    /// <summary>Replaces or deletes the complete group identified by <paramref name="groupKey"/>.</summary>
    /// <remarks>
    /// A null payload deletes the group. A non-null payload is borrowed for the call; the caller retains
    /// its reference and must release it. Implementations retaining the immutable payload acquire their
    /// own reference before replacing the previous payload. Invalid keys or payloads leave the group unchanged.
    /// </remarks>
    void SetNodeGroup<TPath>(TPath groupKey, RefCountingMemory? payload) where TPath : struct, IPbtNodePath<TPath>;
}
