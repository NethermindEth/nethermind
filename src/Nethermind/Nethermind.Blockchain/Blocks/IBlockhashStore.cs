// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.State;

namespace Nethermind.Blockchain.Blocks;

public interface IBlockhashStore
{
    public void ApplyBlockhashStateChanges(BlockHeader blockHeader, IWorldState worldState);
    public Hash256? GetBlockHashFromState(BlockHeader currentBlockHeader, long requiredBlockNumber, IWorldState worldState);
}
