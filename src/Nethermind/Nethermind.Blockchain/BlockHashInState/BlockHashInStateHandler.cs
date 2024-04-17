// SPDX-FileCopyrightText:2023 Demerzel Solutions Limited
// SPDX-License-Identifier:LGPL-3.0-only

using System;
using System.IO;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.State;

namespace Nethermind.Blockchain.BlockHashInState;

public interface IBlockHashInStateHandler
{
    public void InitHistoryOnForkBlock(IBlockTree blockTree, BlockHeader currentBlock, IReleaseSpec spec, IWorldState stateProvider);
    public void AddParentBlockHashToState(BlockHeader blockHeader, IReleaseSpec spec, IWorldState stateProvider);
}

public class BlockHashInStateHandler : IBlockHashInStateHandler
{
    public void InitHistoryOnForkBlock(IBlockTree blockTree, BlockHeader currentBlock, IReleaseSpec spec, IWorldState stateProvider)
    {
        long current = currentBlock.Number;
        BlockHeader header = currentBlock;
        for (var i = 0; i < Math.Min(Eip2935Constants.RingBufferSize, current); i++)
        {
            // an extra check - don't think it is needed
            if (header.IsGenesis) break;
            AddParentBlockHashToState(header, spec, stateProvider);
            header = blockTree.FindParentHeader(currentBlock, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
            if (header is null)
            {
                throw new InvalidDataException(
                    "Parent header cannot be found when initializing BlockHashInState history");
            }
        }
    }

    public void AddParentBlockHashToState(BlockHeader blockHeader, IReleaseSpec spec, IWorldState stateProvider)
    {
        if (!spec.IsEip2935Enabled ||
            blockHeader.IsGenesis ||
            blockHeader.ParentHash is null) return;
        Address? eip2935Account = spec.Eip2935ContractAddress ?? Eip2935Constants.BlockHashHistoryAddress;

        if (!stateProvider.AccountExists(eip2935Account))
            return;

        Hash256 parentBlockHash = blockHeader.ParentHash;
        var blockIndex = new UInt256((ulong)((blockHeader.Number - 1) % Eip2935Constants.RingBufferSize));

        StorageCell blockHashStoreCell = new(eip2935Account, blockIndex);
        stateProvider.Set(blockHashStoreCell, parentBlockHash.Bytes.WithoutLeadingZeros().ToArray());
    }
}
