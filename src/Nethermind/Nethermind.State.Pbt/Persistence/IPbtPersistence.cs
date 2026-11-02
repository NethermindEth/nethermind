// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Pbt;
using Nethermind.State.Flat;
using Nethermind.State.Pbt.Common;

namespace Nethermind.State.Pbt.Persistence;

/// <summary>Durable atomic storage for one canonical EIP-8297 state.</summary>
public interface IPbtPersistence
{
    IReader CreateReader();

    IWriteBatch CreateWriteBatch(in StateId from, in StateId to, in ValueHash256 treeRoot);

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

        /// <summary>Passes the complete group identified by <paramref name="groupKey"/> to <paramref name="action"/>, which must not keep it.</summary>
        /// <returns>Whether the group was found.</returns>
        bool TryLoadNodeGroup<TPath, TArg>(in TPath groupKey, ReadOnlySpanAction<byte, TArg> action, TArg arg)
            where TPath : struct, IPbtNodePath<TPath> where TArg : allows ref struct;
    }

    public interface IWriteBatch : IDisposable
    {
        void SetAccount(in ValueHash256 addressHash, PbtAccount? account);
        /// <summary>Stages the whole run keyed by <paramref name="runKey"/> (a <see cref="SlotRun.RunKey"/>); an empty run deletes it. The run is borrowed for the call.</summary>
        /// <typeparam name="TKey"><see cref="PbtPath"/> for a header-slot run, <see cref="PbtStoragePath"/> for any other.</typeparam>
        void SetSlotRun<TKey>(in TKey runKey, PackedSlotRun run) where TKey : struct, IPbtKey<TKey>;
        void SetCode(in ValueHash256 codeHash, CodeInfo code);
        /// <summary>Stages a complete group replacement; an empty payload deletes the group. The payload is borrowed for the call.</summary>
        void SetNodeGroup<TPath>(TPath groupKey, scoped ReadOnlySpan<byte> payload) where TPath : struct, IPbtNodePath<TPath>;
        void Commit();
    }
}
