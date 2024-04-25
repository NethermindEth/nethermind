// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Blocks;

public interface IBlockhashStore
{
    public void InitHistoryOnForkBlock(BlockHeader currentBlock);
    public void AddParentBlockHashToState(BlockHeader blockHeader);
    public Hash256? GetBlockHashFromState(BlockHeader currentBlockHeader, long requiredBlockNumber);
}
