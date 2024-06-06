// SPDX-FileCopyrightText:2023 Demerzel Solutions Limited
// SPDX-License-Identifier:LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.State;

[assembly: InternalsVisibleTo("Nethermind.Blockchain.Test")]
[assembly: InternalsVisibleTo("Nethermind.Merge.Plugin.Test")]
namespace Nethermind.Blockchain.Blocks;

public class BlockhashStore( ISpecProvider specProvider, IWorldState worldState)
    : IBlockhashStore
{
    private static readonly byte[] EmptyBytes = [0];

    public void ApplyHistoryBlockHashes(BlockHeader blockHeader)
    {
        IReleaseSpec spec = specProvider.GetSpec(blockHeader);
        if (!spec.IsEip2935Enabled || blockHeader.IsGenesis || blockHeader.ParentHash is null) return;

        Address eip2935Account = spec.Eip2935ContractAddress;
        if (!worldState.AccountExists(eip2935Account)) return;

        AddParentBlockHashToState(blockHeader, eip2935Account);
    }

    public Hash256? GetBlockHashFromState(BlockHeader currentHeader, long requiredBlockNumber)
    {
        IReleaseSpec? spec = specProvider.GetSpec(currentHeader);
        if (requiredBlockNumber >= currentHeader.Number ||
            requiredBlockNumber + Eip2935Constants.RingBufferSize < currentHeader.Number)
        {
            return null;
        }
        var blockIndex = new UInt256((ulong)(requiredBlockNumber % Eip2935Constants.RingBufferSize));
        Address eip2935Account = spec.Eip2935ContractAddress;
        StorageCell blockHashStoreCell = new(eip2935Account, blockIndex);
        ReadOnlySpan<byte> data = worldState.Get(blockHashStoreCell);
        return data.SequenceEqual(EmptyBytes) ? null : new Hash256(data);
    }

    private void AddParentBlockHashToState(BlockHeader blockHeader, Address eip2935Account)
    {
        Hash256 parentBlockHash = blockHeader.ParentHash;
        var blockIndex = new UInt256((ulong)((blockHeader.Number - 1) % Eip2935Constants.RingBufferSize));
        StorageCell blockHashStoreCell = new(eip2935Account, blockIndex);
        worldState.Set(blockHashStoreCell, parentBlockHash!.Bytes.WithoutLeadingZeros().ToArray());
    }
}
