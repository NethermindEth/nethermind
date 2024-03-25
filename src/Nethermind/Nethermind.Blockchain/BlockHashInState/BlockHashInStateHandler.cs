// SPDX-FileCopyrightText:2023 Demerzel Solutions Limited
// SPDX-License-Identifier:LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.State;
using Nethermind.Verkle.Tree.Utils;

namespace Nethermind.Blockchain.BlockHashInState;

public interface IBlockHashInStateHandler
{
    public void AddParentBlockHashToState(BlockHeader blockHeader, IReleaseSpec spec, IWorldState stateProvider, IBlockTracer blockTracer);
}

public class BlockHashInStateHandler : IBlockHashInStateHandler
{

    public void AddParentBlockHashToState(BlockHeader blockHeader, IReleaseSpec spec, IWorldState stateProvider, IBlockTracer blockTracer)
    {
        if (!spec.IsEip2935Enabled ||
            blockHeader.IsGenesis ||
            blockHeader.ParentHash is null) return;
        Address? eip2935Account = spec.Eip2935ContractAddress ?? Eip2935Constants.BlockHashHistoryAddress;

        Hash256 parentBlockHash = blockHeader.ParentHash;
        var blockIndex = new UInt256((ulong)blockHeader.Number - 1);

        StorageCell blockHashStoreCell = new(eip2935Account, blockIndex);
        // TODO: this is just for kaustinen right now
        // if (!stateProvider.AccountExists(eip2935Account)) stateProvider.CreateAccount(eip2935Account, 0);
        if (blockHeader.Number == 1) stateProvider.CreateAccount(eip2935Account, 0);
        stateProvider.Set(blockHashStoreCell, parentBlockHash.Bytes.WithoutLeadingZeros().ToArray());


        var blockHashWitness = new VerkleWitness();
        blockHashWitness.AccessCompleteAccount(eip2935Account);
        blockHashWitness.AccessStorage(eip2935Account, blockIndex, true);
        blockTracer.ReportAccessWitness(blockHashWitness);
    }
}
