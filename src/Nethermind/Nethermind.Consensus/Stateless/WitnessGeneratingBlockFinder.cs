// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.Stateless;

public class WitnessGeneratingBlockFinder(IBlockFinder inner) : IBlockFinder
{
    public Hash256 HeadHash => throw new NotSupportedException();
    public Hash256 GenesisHash => throw new NotSupportedException();
    public Hash256? PendingHash => throw new NotSupportedException();
    public Hash256? FinalizedHash => throw new NotSupportedException();
    public Hash256? SafeHash => throw new NotSupportedException();
    public Block? Head => throw new NotSupportedException();
    public Block? FindBlock(Hash256 blockHash, BlockTreeLookupOptions options, long? blockNumber = null)
        => throw new NotSupportedException();

    public Block? FindBlock(long blockNumber, BlockTreeLookupOptions options)
        => throw new NotSupportedException();

    public bool HasBlock(long blockNumber, Hash256 blockHash)
        => throw new NotSupportedException();

    private long? _lowestRequestedHeader = null;

    public byte[][] GetWitnessHeaders(Hash256 parentHash)
    {
        HeaderDecoder decoder = new();
        List<byte[]> headers = new();
        BlockHeader parent = inner.FindHeader(parentHash) ?? throw new ArgumentException($"Parent {parentHash} is not found");
        if (_lowestRequestedHeader is not null)
        {
            for (long i = _lowestRequestedHeader.Value; i < parent.Number; i++)
            {
                headers.Add(
                    decoder.Encode(
                        inner.FindHeader(i) ?? throw new ArgumentException("Unable to get requested header during witness generation")).Bytes);
            }
        }
        headers.Add(decoder.Encode(parent).Bytes);
        return headers.ToArray();
    }

    public BlockHeader? FindHeader(Hash256 blockHash, BlockTreeLookupOptions options, long? blockNumber = null)
    {
        BlockHeader? header = inner.FindHeader(blockHash, options, blockNumber);
        if (header is not null && (_lowestRequestedHeader ?? long.MaxValue) > header.Number)
        {
            _lowestRequestedHeader = header.Number;
        }
        return header;
    }

    public BlockHeader? FindHeader(long blockNumber, BlockTreeLookupOptions options)
    {
        BlockHeader? header = inner.FindHeader(blockNumber, options);
        if (header is not null && (_lowestRequestedHeader ?? long.MaxValue) > header.Number)
        {
            _lowestRequestedHeader = header.Number;
        }
        return header;
    }

    public Hash256? FindBlockHash(long blockNumber)
        => throw new NotSupportedException();

    public bool IsMainChain(BlockHeader blockHeader)
        => throw new NotSupportedException();

    public bool IsMainChain(Hash256 blockHash, bool throwOnMissingHash = true)
        => throw new NotSupportedException();

    public BlockHeader FindBestSuggestedHeader()
        => throw new NotSupportedException();

    public long GetLowestBlock()
        => throw new NotSupportedException();

    public long? BestPersistedState
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }
}
