// SPDX-FileCopyrightText:2023 Demerzel Solutions Limited
// SPDX-License-Identifier:LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.State;

namespace Nethermind.Blockchain.BlockHashInState;

public interface IBlockHashInStateHandler
{
    public void AddBlockHashToState(Block block, IReleaseSpec spec, IWorldState stateProvider);
}

public class BlockHashInStateHandler: IBlockHashInStateHandler
{

    public void AddBlockHashToState(Block block, IReleaseSpec spec, IWorldState stateProvider)
    {
        if (!spec.IsEip2935Enabled ||
            block.IsGenesis ||
            block.Header.ParentHash is null) return;
        Address? eip2935Account = spec.Eip2935ContractAddress ?? Eip2935Constants.BlockHashHistoryAddress;

        Hash256 parentBlockHash = block.Header.ParentHash;
        var blockIndex = new UInt256((ulong)block.Number - 1);

        StorageCell blockHashStoreCell = new(eip2935Account, blockIndex);
        if(!stateProvider.AccountExists(eip2935Account)) stateProvider.CreateAccount(eip2935Account, 0);
        stateProvider.Set(blockHashStoreCell, parentBlockHash.Bytes.WithoutLeadingZeros().ToArray());
    }
}
