// SPDX-FileCopyrightText:2023 Demerzel Solutions Limited
// SPDX-License-Identifier:LGPL-3.0-only

using System;
using System.IO;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.State;

namespace Nethermind.Blockchain;

public interface IBlockhashStore
{
    public void InitHistoryOnForkBlock(BlockHeader currentBlock);
    public void AddParentBlockHashToState(BlockHeader blockHeader);
    public Hash256? GetBlockHashFromState(BlockHeader currentBlockHeader, long requiredBlockNumber);
}
public class BlockhashStore: IBlockhashStore
{
    public BlockhashStore(IBlockFinder blockFinder, ISpecProvider specProvider, IWorldState worldState)
    {
        _blockFinder = blockFinder;
        _specProvider = specProvider;
        _worldState = worldState;
    }
    private readonly IBlockFinder _blockFinder;
    private readonly ISpecProvider _specProvider;
    private readonly IWorldState _worldState;
    public void InitHistoryOnForkBlock(BlockHeader currentBlock)
    {
        long current = currentBlock.Number;
        BlockHeader header = currentBlock;
        for (var i = 0; i < Math.Min(Eip2935Constants.RingBufferSize, current); i++)
        {
            // an extra check - don't think it is needed
            if (header.IsGenesis) break;
            AddParentBlockHashToState(header);
            header = _blockFinder.FindParentHeader(header, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
            if (header is null)
            {
                throw new InvalidDataException(
                    "Parent header cannot be found when initializing BlockHashInState history");
            }
        }
    }
    public void AddParentBlockHashToState(BlockHeader blockHeader)
    {
        var spec = _specProvider.GetSpec(blockHeader);
        if (!spec.IsEip2935Enabled || blockHeader.IsGenesis || blockHeader.ParentHash is null) return;
        Address? eip2935Account = spec.Eip2935ContractAddress ?? Eip2935Constants.BlockHashHistoryAddress;

        if (!_worldState.AccountExists(eip2935Account)) return;

        Hash256 parentBlockHash = blockHeader.ParentHash;
        var blockIndex = new UInt256((ulong)((blockHeader.Number - 1) % Eip2935Constants.RingBufferSize));

        StorageCell blockHashStoreCell = new(eip2935Account, blockIndex);
        _worldState.Set(blockHashStoreCell, parentBlockHash.Bytes.WithoutLeadingZeros().ToArray());
    }

    public Hash256? GetBlockHashFromState(BlockHeader currentHeader, long requiredBlockNumber)
    {
        var spec = _specProvider.GetSpec(currentHeader);
        if (requiredBlockNumber >= currentHeader.Number ||
            requiredBlockNumber + Eip2935Constants.RingBufferSize < currentHeader.Number)
        {
            return null;
        }
        var blockIndex = new UInt256((ulong)(requiredBlockNumber % Eip2935Constants.RingBufferSize));
        Address? eip2935Account = spec.Eip2935ContractAddress ?? Eip2935Constants.BlockHashHistoryAddress;
        StorageCell blockHashStoreCell = new(eip2935Account, blockIndex);
        ReadOnlySpan<byte> data = _worldState.Get(blockHashStoreCell);
        return data.Length < 32 ? null : new Hash256(data);
    }
}
