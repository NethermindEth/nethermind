// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Merge.AuRa.Shutter.Contracts;

public interface IKeyperSetContract
{
    /// <summary>
    /// Check if the keyper set contract has been finalized
    /// </summary>
    bool IsFinalized(BlockHeader blockHeader);

    /// <summary>
    /// Gets the keyper set threshold
    /// </summary>
    ulong GetThreshold(BlockHeader blockHeader);

    /// <summary>
    /// Gets the members of the keyper set
    /// </summary>
    Address[] GetMembers(BlockHeader blockHeader);
}
