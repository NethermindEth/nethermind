// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.State;

namespace Nethermind.Blockchain.BlockHashInState;

public interface IBlockHashInStateHandler
{
    public void InitHistoryOnForkBlock(IBlockTree blockTree, BlockHeader currentBlock, IReleaseSpec spec, IWorldState stateProvider);
    public void AddParentBlockHashToState(BlockHeader blockHeader, IReleaseSpec spec, IWorldState stateProvider);
}
