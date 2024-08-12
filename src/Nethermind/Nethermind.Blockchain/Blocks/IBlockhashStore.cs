// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;

namespace Nethermind.Blockchain.Blocks;

public interface IBlockhashStore
{
    public void ApplyBlockhashStateChanges(BlockHeader blockHeader, ITxTracer? txTracer = null);
    public Hash256? GetBlockHashFromState(BlockHeader currentBlockHeader, long requiredBlockNumber);
}
