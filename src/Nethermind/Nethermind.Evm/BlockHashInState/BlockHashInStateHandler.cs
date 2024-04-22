// SPDX-FileCopyrightText:2023 Demerzel Solutions Limited
// SPDX-License-Identifier:LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.State;

namespace Nethermind.Evm.BlockHashInState;

public static class BlockHashInStateHandler
{
    public static void AddParentBlockHashToState(BlockHeader blockHeader, IReleaseSpec spec, IWorldState stateProvider)
    {
        if (!spec.IsEip2935Enabled || blockHeader.IsGenesis || blockHeader.ParentHash is null) return;
        Address? eip2935Account = spec.Eip2935ContractAddress ?? Eip2935Constants.BlockHashHistoryAddress;

        if (!stateProvider.AccountExists(eip2935Account)) return;

        Hash256 parentBlockHash = blockHeader.ParentHash;
        var blockIndex = new UInt256((ulong)((blockHeader.Number - 1) % Eip2935Constants.RingBufferSize));

        StorageCell blockHashStoreCell = new(eip2935Account, blockIndex);
        stateProvider.Set(blockHashStoreCell, parentBlockHash.Bytes.WithoutLeadingZeros().ToArray());
    }

    public static Hash256? GetBlockHashFromState(long currentBlockNumber, long requiredBlockNumber, IReleaseSpec spec, IWorldState stateProvider)
    {
        if (requiredBlockNumber >= currentBlockNumber ||
            requiredBlockNumber + Eip2935Constants.RingBufferSize < currentBlockNumber)
        {
            return null;
        }
        var blockIndex = new UInt256((ulong)(requiredBlockNumber % Eip2935Constants.RingBufferSize));
        Address? eip2935Account = spec.Eip2935ContractAddress ?? Eip2935Constants.BlockHashHistoryAddress;
        StorageCell blockHashStoreCell = new(eip2935Account, blockIndex);
        ReadOnlySpan<byte> data = stateProvider.Get(blockHashStoreCell);
        return data.Length < 32 ? null : new Hash256(data);
    }
}
