// SPDX-FileCopyrightText:2023 Demerzel Solutions Limited
// SPDX-License-Identifier:LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Int256;

[assembly: InternalsVisibleTo("Nethermind.Blockchain.Test")]
[assembly: InternalsVisibleTo("Nethermind.Merge.Plugin.Test")]
namespace Nethermind.Blockchain.Blocks;

public class BlockhashStore(IWorldState worldState) : IBlockhashStore, IHasAccessList
{
    private static readonly byte[] EmptyBytes = [0];

    public void ApplyBlockhashStateChanges(BlockHeader blockHeader, IReleaseSpec spec)
    {
        if (!TryGetParentHashCell(blockHeader, spec, out StorageCell blockHashStoreCell)) return;

        worldState.Set(blockHashStoreCell, blockHeader.ParentHash!.Bytes.WithoutLeadingZeros().ToArray());
        worldState.RecordBytecodeAccess(blockHashStoreCell.Address);
    }

    public AccessList? GetAccessList(Block block, IReleaseSpec spec) =>
        TryGetParentHashCell(block.Header, spec, out StorageCell blockHashStoreCell)
            ? AccessList.ForSingleStorageCell(in blockHashStoreCell)
            : null;

    private bool TryGetParentHashCell(BlockHeader header, IReleaseSpec spec, out StorageCell blockHashStoreCell)
    {
        blockHashStoreCell = default;
        if (!spec.IsEip2935Enabled || header.IsGenesis || header.ParentHash is null) return false;

        Address eip2935Account = spec.Eip2935ContractAddress ?? Eip2935Constants.BlockHashHistoryAddress;
        if (!worldState.IsContract(eip2935Account)) return false;

        blockHashStoreCell = new StorageCell(eip2935Account, new UInt256((ulong)(header.Number - 1) % spec.Eip2935RingBufferSize));
        return true;
    }

    public Hash256? GetBlockHashFromState(BlockHeader currentHeader, ulong requiredBlockNumber, IReleaseSpec spec)
    {
        if (requiredBlockNumber >= currentHeader.Number ||
            requiredBlockNumber + spec.Eip2935RingBufferSize < currentHeader.Number)
        {
            return null;
        }
        UInt256 blockIndex = new(requiredBlockNumber % spec.Eip2935RingBufferSize);
        Address? eip2935Account = spec.Eip2935ContractAddress ?? Eip2935Constants.BlockHashHistoryAddress;
        StorageCell blockHashStoreCell = new(eip2935Account, blockIndex);
        ReadOnlySpan<byte> data = worldState.Get(blockHashStoreCell);
        return data.SequenceEqual(EmptyBytes) ? null : Hash256.FromBytesWithPadding(data);
    }
}
