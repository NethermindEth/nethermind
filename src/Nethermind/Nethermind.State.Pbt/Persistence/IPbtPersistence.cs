// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt.Persistence;

/// <summary>Durable atomic storage for one canonical EIP-8297 state.</summary>
public interface IPbtPersistence
{
    IReader CreateReader();

    IWriteBatch CreateWriteBatch(in StateId from, in StateId to, in ValueHash256 treeRoot, WriteFlags flags);
    IWriteBatch CreateStagingWriteBatch(WriteFlags flags);

    void Flush();

    public interface IReader : IDisposable
    {
        StateId CurrentState { get; }
        ValueHash256 CurrentRoot { get; }

        ValueHash256? GetLeaf(PbtFullKey key);
        IEnumerable<KeyValuePair<PbtFullKey, ValueHash256>> EnumerateLeaves();
        IEnumerable<KeyValuePair<PbtFullKey, ValueHash256>> EnumerateLeaves(PbtFullKey prefix);

        /// <summary>Gets a caller-owned lease for the complete group identified by <paramref name="groupKey"/>.</summary>
        /// <remarks>
        /// A non-null result transfers exactly one reference to the caller, which must invoke
        /// <see cref="IDisposable.Dispose"/> exactly once. Consumers must treat the memory returned by
        /// <see cref="RefCountingMemory.GetSpan"/> as read-only. The reference keeps the payload valid until
        /// released, independently of the reader. A missing group returns <see langword="null"/>. Implementations
        /// validate that <paramref name="groupKey"/> is at a four-level boundary even when the group is absent.
        /// </remarks>
        /// <param name="groupKey">The four-level-boundary key identifying the group.</param>
        /// <returns>One caller-owned reference, or <see langword="null"/> when the group is absent.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="groupKey"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="groupKey"/> is not at a four-level boundary.</exception>
        RefCountingMemory? GetNodeGroup(PbtNodePath groupKey);

        /// <summary>Enumerates group keys in ascending <see cref="PbtNodePath.CompareTo"/> order.</summary>
        IEnumerable<PbtNodePath> EnumerateNodeGroupKeys();

        ulong GetCodeReference(in ValueHash256 codeHash);
    }

    public interface IWriteBatch : IDisposable
    {
        void SetLeaf(PbtFullKey key, ValueHash256? value);
        /// <summary>Stages a complete group replacement, or deletes the group when the payload is null.</summary>
        /// <remarks>
        /// The payload is borrowed for this call and its bytes must remain immutable. The caller retains
        /// its reference; an implementation retaining the payload must acquire an independent reference.
        /// Validates the boundary key and complete payload before staging the write.
        /// </remarks>
        void SetNodeGroup(PbtNodePath groupKey, RefCountingMemory? payload);
        void SetCodeReference(in ValueHash256 codeHash, ulong? referenceCount);
        void Commit();
    }
}
