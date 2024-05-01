// SPDX-FileCopyrightText:2023 Demerzel Solutions Limited
// SPDX-License-Identifier:LGPL-3.0-only

using System;
using System.IO;
using System.Runtime.CompilerServices;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.State;

[assembly: InternalsVisibleTo("Nethermind.Blockchain.Test")]
[assembly: InternalsVisibleTo("Nethermind.Merge.Plugin.Test")]
namespace Nethermind.Blockchain.Blocks;

public class BlockhashStore(IBlockFinder blockFinder, ISpecProvider specProvider, IWorldState worldState)
    : IBlockhashStore
{
    private static readonly byte[] EmptyBytes = [0];

    public void ApplyHistoryBlockHashes(BlockHeader blockHeader)
    {
        IReleaseSpec spec = specProvider.GetSpec(blockHeader);
        if (!spec.IsEip2935Enabled || blockHeader.IsGenesis || blockHeader.ParentHash is null) return;

        Address? eip2935Account = spec.Eip2935ContractAddress ?? Eip2935Constants.BlockHashHistoryAddress;
        if (!worldState.AccountExists(eip2935Account)) return;

        // TODO: find a better way to handle this - no need to have this check everytime
        //      this would just be true on the fork block
        BlockHeader parentHeader = blockFinder.FindParentHeader(blockHeader, BlockTreeLookupOptions.None);
        if (parentHeader is not null && parentHeader!.Timestamp < spec.Eip2935TransitionTimestamp)
            InitHistoryOnForkBlock(blockHeader, eip2935Account);
        else
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
        Address? eip2935Account = spec.Eip2935ContractAddress ?? Eip2935Constants.BlockHashHistoryAddress;
        StorageCell blockHashStoreCell = new(eip2935Account, blockIndex);
        ReadOnlySpan<byte> data = worldState.Get(blockHashStoreCell);
        return data.SequenceEqual(EmptyBytes) ? null : new Hash256(data);
    }

    private void InitHistoryOnForkBlock(BlockHeader currentBlock, Address eip2935Account)
    {
        long current = currentBlock.Number;
        BlockHeader header = currentBlock;
        for (var i = 0; i < Math.Min(Eip2935Constants.RingBufferSize, current); i++)
        {
            // an extra check - don't think it is needed
            if (header.IsGenesis) break;
            AddParentBlockHashToState(header, eip2935Account);
            header = blockFinder.FindParentHeader(header, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
            if (header is null)
            {
                throw new InvalidDataException(
                    "Parent header cannot be found when initializing BlockHashInState history");
            }
        }
    }

    private void AddParentBlockHashToState(BlockHeader blockHeader, Address eip2935Account)
    {
        Hash256 parentBlockHash = blockHeader.ParentHash;
        var blockIndex = new UInt256((ulong)((blockHeader.Number - 1) % Eip2935Constants.RingBufferSize));
        StorageCell blockHashStoreCell = new(eip2935Account, blockIndex);
        worldState.Set(blockHashStoreCell, parentBlockHash!.BytesToArray());
    }
}
