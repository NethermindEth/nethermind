// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Evm.CodeAnalysis;
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

        Account? GetAccount(in ValueHash256 addressHash);
        EvmWord GetSlot(PbtStorageFullKey key);
        CodeInfo? GetCode(in ValueHash256 codeHash);
        IEnumerable<KeyValuePair<ValueHash256, Account>> EnumerateAccounts();
        IEnumerable<KeyValuePair<PbtStorageFullKey, EvmWord>> EnumerateStorage(PbtStorageFullKey? prefix = null);

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
        /// <exception cref="ArgumentException"><paramref name="groupKey"/> is not at a four-level boundary.</exception>
        RefCountingMemory? GetNodeGroup<TPath>(TPath groupKey) where TPath : struct, IPbtNodePath<TPath>;

        /// <summary>Enumerates group keys in ascending <see cref="PbtStorageNodePath.CompareTo"/> order.</summary>
        IEnumerable<PbtStorageNodePath> EnumerateNodeGroupKeys();

        ulong GetCodeReference(in ValueHash256 codeHash);
    }

    public interface IWriteBatch : IDisposable
    {
        void SetAccount(in ValueHash256 addressHash, Account? account);
        void SetSlot(PbtStorageFullKey key, in EvmWord value);
        void SetCode(in ValueHash256 codeHash, CodeInfo code);
        void ClearStorage(in ValueHash256 addressHash);
        /// <summary>Stages a complete group replacement, or deletes the group when the payload is null.</summary>
        /// <remarks>
        /// The payload is borrowed for this call and its bytes must remain immutable. The caller retains
        /// its reference; an implementation retaining the payload must acquire an independent reference.
        /// Validates the boundary key and complete payload before staging the write.
        /// </remarks>
        void SetNodeGroup<TPath>(TPath groupKey, RefCountingMemory? payload) where TPath : struct, IPbtNodePath<TPath>;
        void SetCodeReference(in ValueHash256 codeHash, ulong? referenceCount);
        void Commit();
    }
}
