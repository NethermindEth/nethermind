// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Merge.AuRa.Shutter.Contracts;

public interface IKeyperSetManagerContract
{
    /// <summary>
    /// Gets the keyper set contract address from index (eon).
    /// </summary>
    /// <param name="eon"></param>
    (Address, ulong) GetKeyperSetAddress(BlockHeader blockHeader, in ulong index);

    /// <summary>
    /// Gets the current eon.
    /// </summary>
    ulong GetNumKeyperSets(BlockHeader blockHeader);


    /// <summary>
    /// Gets the keyper set contract address from slot.
    /// </summary>
    /// <param name="eon"></param>
    (Address, ulong) GetKeyperSetIndexBySlot(BlockHeader blockHeader, in ulong slot);
}
