// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Shutter.Contracts;

public interface IKeyperSetManagerContract
{
    /// <summary>
    /// Gets the keyper set contract address from index (eon).
    /// </summary>
    /// <param name="index"></param>
    Address GetKeyperSetAddress(BlockHeader blockHeader, in ulong index);

    /// <summary>
    /// Gets the current eon.
    /// </summary>
    ulong GetNumKeyperSets(BlockHeader blockHeader);


    /// <summary>
    /// Gets the keyper set contract address from block number.
    /// </summary>
    /// <param name="blockNumber"></param>
    ulong GetKeyperSetIndexByBlock(BlockHeader blockHeader, in ulong blockNumber);
}
