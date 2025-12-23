// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.Stateless;

public class WitnessGeneratingBlockHashProvider(IBlockhashProvider blockHashProvider, IReadOnlyBlockTree blockTree) : IBlockhashProvider
{
    private long _lowestRequestedHeader = long.MaxValue;
    public Hash256? GetBlockhash(BlockHeader currentBlock, long number, IReleaseSpec spec)
    {
        Hash256? hash = blockHashProvider.GetBlockhash(currentBlock, number, spec);
        if (hash is not null) _lowestRequestedHeader = Math.Min(_lowestRequestedHeader, number);
        return hash;
    }

    public Task Prefetch(BlockHeader currentBlock, CancellationToken token)
    {
        return blockHashProvider.Prefetch(currentBlock, token);
    }

    public byte[][] GetWitnessHeaders(Hash256 parentHash)
    {
        HeaderDecoder decoder = new();
        List<byte[]> headers = [];
        BlockHeader parent = blockTree.FindHeader(parentHash) ?? throw new ArgumentException($"Parent {parentHash} is not found");
        if (_lowestRequestedHeader is not long.MaxValue)
        {
            for (long i = _lowestRequestedHeader; i < parent.Number; i++)
            {
                headers.Add(
                    decoder.Encode(
                        blockTree.FindHeader(i) ?? throw new ArgumentException("Unable to get requested header during witness generation")).Bytes);
            }
        }
        headers.Add(decoder.Encode(parent).Bytes);
        return headers.ToArray();
    }
}
