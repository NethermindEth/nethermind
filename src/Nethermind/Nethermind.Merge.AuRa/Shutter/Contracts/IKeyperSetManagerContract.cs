// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Merge.AuRa.Shutter.Contracts;

public interface IKeyperSetManagerContract
{
    /// <summary>
    /// Gets the keyper address from its index.
    /// </summary>
    /// <param name="eon"></param>
    (Address, ulong) GetKeyperSetAddress(BlockHeader blockHeader, in ulong index);
}
