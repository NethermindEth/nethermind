// SPDX-FileCopyrightText:2023 Demerzel Solutions Limited
// SPDX-License-Identifier:LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Int256;

[assembly: InternalsVisibleTo("Nethermind.Blockchain.Test")]
[assembly: InternalsVisibleTo("Nethermind.Merge.Plugin.Test")]
namespace Nethermind.Blockchain.Blocks;

public class BlockhashStore(IWorldState worldState) : IBlockhashStore
{
    private static readonly byte[] EmptyBytes = [0];

    public void ApplyBlockhashStateChanges(BlockHeader blockHeader, IReleaseSpec spec)
    {
        if (!spec.IsEip2935Enabled || blockHeader.IsGenesis || blockHeader.ParentHash is null) return;

        Address? eip2935Account = spec.Eip2935ContractAddress ?? Eip2935Constants.BlockHashHistoryAddress;
        if (!worldState.IsContract(eip2935Account)) return;

        Hash256 parentBlockHash = blockHeader.ParentHash;
        ulong ringBufferSize = checked((ulong)spec.Eip2935RingBufferSize);
        UInt256 parentBlockIndex = new UInt256((blockHeader.Number - 1) % ringBufferSize);
        StorageCell blockHashStoreCell = new(eip2935Account, parentBlockIndex);
        worldState.Set(blockHashStoreCell, parentBlockHash!.Bytes.WithoutLeadingZeros().ToArray());
    }

    public Hash256? GetBlockHashFromState(BlockHeader currentHeader, long requiredBlockNumber, IReleaseSpec spec)
    {
        if (requiredBlockNumber < 0)
        {
            return null;
        }

        ulong required = (ulong)requiredBlockNumber;
        ulong ringBufferSize = checked((ulong)spec.Eip2935RingBufferSize);
        if (required >= currentHeader.Number || required + ringBufferSize < currentHeader.Number)
        {
            return null;
        }

        UInt256 blockIndex = new UInt256(required % ringBufferSize);
        Address? eip2935Account = spec.Eip2935ContractAddress ?? Eip2935Constants.BlockHashHistoryAddress;
        StorageCell blockHashStoreCell = new(eip2935Account, blockIndex);
        ReadOnlySpan<byte> data = worldState.Get(blockHashStoreCell);
        return data.SequenceEqual(EmptyBytes) ? null : Hash256.FromBytesWithPadding(data);
    }
}
