// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.State;

namespace Nethermind.Evm.BlockHashInState;

public interface IBlockHashInStateHandler
{
    public void AddParentBlockHashToState(BlockHeader blockHeader, IReleaseSpec spec, IWorldState stateProvider);
    public Hash256? GetBlockHashFromState(long blockNumber, IReleaseSpec spec, IWorldState stateProvider);
}
