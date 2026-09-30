// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.State.Pbt.Test;

internal static class StorageReadExtensions
{
    public static UInt256 Get(this IWorldStateScopeProvider.IStorageTree tree, in UInt256 index)
    {
        tree.Get(in index, out UInt256 value);
        return value;
    }

    public static UInt256 Get(this IWorldState worldState, in StorageCell cell)
    {
        worldState.Get(in cell, out UInt256 value);
        return value;
    }

    public static UInt256 GetStorage(this IStateReader reader, BlockHeader? baseBlock, Address address, in UInt256 index)
    {
        reader.GetStorage(baseBlock, address, in index, out UInt256 value);
        return value;
    }
}
