// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Memory;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Pbt;
using Nethermind.State.Flat;

namespace Nethermind.State.Pbt.Persistence;

/// <summary>Durable atomic storage for one canonical EIP-8297 state.</summary>
public interface IPbtPersistence
{
    IReader CreateReader();

    IWriteBatch CreateWriteBatch(in StateId from, in StateId to, in ValueHash256 treeRoot, WriteFlags flags);

    void Flush();

    /// <summary>Drops cached reads so the next reader observes writes made behind this persistence.</summary>
    void ClearCaches() { }

    public interface IReader : IDisposable
    {
        StateId CurrentState { get; }
        ValueHash256 CurrentRoot { get; }

        PbtAccount? GetAccount(in ValueHash256 addressHash);
        /// <summary>Gets a caller-owned copy of the persisted run keyed by <paramref name="runKey"/> (a <see cref="SlotRun.RunKey"/>); <see cref="SlotRun.Empty"/> when absent.</summary>
        /// <typeparam name="TKey"><see cref="PbtPath"/> for a header-slot run, <see cref="PbtStoragePath"/> for any other.</typeparam>
        PackedSlotRun GetSlotRun<TKey>(in TKey runKey) where TKey : struct, IPbtKey<TKey>;
        CodeInfo? GetCode(in ValueHash256 codeHash);
        /// <summary>Gets the code-chunk leaf keyed by <paramref name="key"/>; false when absent, which an all-zero chunk always is.</summary>
        bool TryGetCodeLeaf(in PbtPath key, out ValueHash256 value);
        /// <summary>Gets a caller-owned, single-pass iterator over persisted accounts in address-hash order.</summary>
        /// <remarks>Dispose the iterator before disposing its reader, including when iteration ends early or throws.</remarks>
        IEnumerator<KeyValuePair<ValueHash256, PbtAccount>> EnumerateAccounts();

        /// <summary>Gets a caller-owned lease for the complete group identified by <paramref name="groupKey"/>.</summary>
        /// <remarks>
        /// A non-null result transfers exactly one reference to the caller, which must invoke
        /// <see cref="IDisposable.Dispose"/> exactly once. Consumers must treat the memory returned by
        /// <see cref="RefCountingMemory.GetSpan"/> as read-only. The reference keeps the payload valid until
        /// released, independently of the reader. A missing group returns <see langword="null"/>.
        /// </remarks>
        /// <param name="groupKey">The four-level-boundary key identifying the group.</param>
        /// <returns>One caller-owned reference, or <see langword="null"/> when the group is absent.</returns>
        RefCountingMemory? GetNodeGroup<TPath>(TPath groupKey) where TPath : struct, IPbtNodePath<TPath>;
    }

    public interface IWriteBatch : IDisposable
    {
        void SetAccount(in ValueHash256 addressHash, PbtAccount? account);
        /// <summary>Stages the whole run keyed by <paramref name="runKey"/> (a <see cref="SlotRun.RunKey"/>); an empty run deletes it. The run is borrowed for the call.</summary>
        /// <typeparam name="TKey"><see cref="PbtPath"/> for a header-slot run, <see cref="PbtStoragePath"/> for any other.</typeparam>
        void SetSlotRun<TKey>(in TKey runKey, PackedSlotRun run) where TKey : struct, IPbtKey<TKey>;
        void SetCode(in ValueHash256 codeHash, CodeInfo code);
        /// <summary>Stages the code-chunk leaf keyed by <paramref name="key"/>; the caller omits all-zero chunks.</summary>
        void SetCodeLeaf(in PbtPath key, in ValueHash256 value);
        /// <summary>Stages a complete group replacement, or deletes the group when the payload is null.</summary>
        /// <remarks>
        /// The payload is borrowed for this call and its bytes must remain immutable. The caller retains
        /// its reference; an implementation retaining the payload must acquire an independent reference.
        /// </remarks>
        void SetNodeGroup<TPath>(TPath groupKey, RefCountingMemory? payload) where TPath : struct, IPbtNodePath<TPath>;
        void Commit();
    }
}
