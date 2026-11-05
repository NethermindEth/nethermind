// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.State.Flat;

namespace Nethermind.FlatPriorityRead;

/// <summary>
/// Holds the slots of the priority contracts and answers point reads from a snapshot of a past commit.
/// </summary>
/// <remarks>
/// A contract is the position of its address in <see cref="IFlatPriorityReadConfig.Addresses"/>; a slot key is the key
/// the flat persistence stores the slot under. A zero value is a stored slot, distinct from an absent one.
/// </remarks>
public interface IPrioritySlotStore : IDisposable
{
    /// <summary>Opens a snapshot of the last commit, which later commits do not change.</summary>
    /// <remarks>The reader may be used by many threads at once.</remarks>
    IReader BeginRead();

    /// <summary>Starts a write. The caller runs one writer at a time.</summary>
    IWriter BeginWrite();

    public interface IReader : IDisposable
    {
        /// <summary>The state passed to the commit this snapshot shows.</summary>
        StateId State { get; }

        /// <returns>Whether the slot is stored.</returns>
        bool TryGet(byte contract, in ValueHash256 slotKey, out UInt256 value);
    }

    public interface IWriter : IDisposable
    {
        void Set(byte contract, in ValueHash256 slotKey, in UInt256 value);
        void Remove(byte contract, in ValueHash256 slotKey);

        /// <summary>Publishes every change made through this writer, together with <paramref name="state"/>, atomically.</summary>
        void Commit(in StateId state);
    }
}
