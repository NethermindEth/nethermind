// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Headers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Core.Collections;

namespace Nethermind.Consensus.Stateless;

public class WitnessGeneratingHeaderFinder(IHeaderFinder inner) : IHeaderFinder
{
    private static readonly HeaderDecoder _decoder = new();
    private long _lowestRequestedHeader = long.MaxValue;

    public BlockHeader? Get(Hash256 blockHash, long? blockNumber = null)
    {
        BlockHeader? header = inner.Get(blockHash, blockNumber);
        if (header is not null && header.Number < _lowestRequestedHeader)
        {
            _lowestRequestedHeader = header.Number;
        }
        return header;
    }

    public IOwnedReadOnlyList<byte[]> GetWitnessHeaders(Hash256 parentHash)
    {
        ArrayPoolList<byte[]> headers = new(capacity: 16);

        Hash256 currentHash = parentHash;
        BlockHeader childHeader = inner.Get(currentHash) ?? throw new ArgumentException($"Parent {currentHash} is not found");
        headers.Add(_decoder.Encode(childHeader).Bytes);

        if (_lowestRequestedHeader < long.MaxValue)
        {
            for (long i = childHeader.Number - 1; i >= _lowestRequestedHeader; i--)
            {
                currentHash = childHeader.ParentHash!;
                childHeader = inner.Get(currentHash, i) ?? throw new ArgumentException($"Unable to get requested header at hash {currentHash} and number {i} during witness generation");
                headers.Add(_decoder.Encode(childHeader).Bytes);
            }
        }

        return headers;
    }
}
