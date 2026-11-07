// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.State.Flat;
using Nethermind.Trie;

namespace Nethermind.FlatPriorityRead;

/// <summary>
/// Holds the slots of the priority contracts, and optionally their storage trie nodes, and answers point reads from a
/// snapshot of a past commit.
/// </summary>
/// <remarks>
/// A contract is the position of its address in <see cref="IFlatPriorityReadConfig.Addresses"/>; a slot key is the key
/// the flat persistence stores the slot under, and a node is keyed by its path in the contract's storage trie. A zero
/// value is a stored slot, distinct from an absent one. Only the Lmdb and RocksDb stores hold trie nodes; the Memory
/// store throws <see cref="NotSupportedException"/> from the node members.
/// </remarks>
public interface IPrioritySlotStore : IDisposable
{
    /// <summary>Opens a snapshot of the last commit, which later commits do not change.</summary>
    /// <remarks>The reader may be used by many threads at once.</remarks>
    IReader BeginRead();

    /// <summary>Starts a write. The caller runs one writer at a time, on one thread.</summary>
    IWriter BeginWrite();

    public interface IReader : IDisposable
    {
        /// <summary>The state passed to the commit this snapshot shows.</summary>
        StateId State { get; }

        /// <returns>Whether the slot is stored.</returns>
        bool TryGet(int contract, in ValueHash256 slotKey, out UInt256 value);

        /// <returns>The RLP of the storage trie node at <paramref name="path"/>, or null when none is stored.</returns>
        byte[]? GetNode(int contract, in TreePath path);

        /// <summary>Passes the RLP of the storage trie node at <paramref name="path"/> to <paramref name="action"/>, which must not keep it.</summary>
        /// <returns>Whether the node is stored.</returns>
        bool TryGetNode<TArg>(int contract, in TreePath path, ReadOnlySpanAction<byte, TArg> action, TArg arg) where TArg : allows ref struct;
    }

    public interface IWriter : IDisposable
    {
        void Set(int contract, in ValueHash256 slotKey, in UInt256 value);
        void Remove(int contract, in ValueHash256 slotKey);
        void SetNode(int contract, in TreePath path, ReadOnlySpan<byte> rlp);
        void RemoveNode(int contract, in TreePath path);

        /// <summary>Publishes every change made through this writer, together with <paramref name="state"/>, atomically.</summary>
        void Commit(in StateId state);
    }
}
