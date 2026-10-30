// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Evm.State;

/// <summary>A guest world state's storage as SSTORE reads and writes it: one lookup of the slot for both.</summary>
public interface IGuestSlotStore
{
    /// <summary>Finds the slot of <paramref name="storageCell"/>, loading it on first use.</summary>
    /// <param name="storageCell">The slot's account and key.</param>
    /// <param name="current">The value the slot holds now, as <see cref="IWorldState.Get(in StorageCell, out UInt256)"/> reads it.</param>
    /// <param name="original">The value at the start of the transaction, as <see cref="IWorldState.GetOriginal"/> reads it.</param>
    /// <returns>The slot's index, for <see cref="WriteAt"/> before any other storage access.</returns>
    int ReadForStore(in StorageCell storageCell, out UInt256 current, out UInt256 original);

    /// <summary>Writes <paramref name="newValue"/> to the slot <see cref="ReadForStore"/> found, as <see cref="IWorldState.Set(in StorageCell, in UInt256)"/> does.</summary>
    void WriteAt(int slot, in UInt256 newValue);
}
