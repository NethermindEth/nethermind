

using System;
using Nethermind.Blockchain.Headers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Core.Collections;

namespace Nethermind.Consensus.Stateless;

public class WitnessGeneratingHeaderFinder(IHeaderFinder inner) : IHeaderFinder
{
    private long? _lowestRequestedHeader = null;

    public BlockHeader? Get(Hash256 blockHash, long? blockNumber = null)
    {
        BlockHeader? header = inner.Get(blockHash, blockNumber);
        if (header is not null && header.Number < (_lowestRequestedHeader ?? long.MaxValue))
        {
            _lowestRequestedHeader = blockNumber;
        }
        return header;
    }

    public byte[][] GetWitnessHeaders(Hash256 parentHash)
    {
        HeaderDecoder decoder = new();
        using ArrayPoolListRef<byte[]> headers = new();

        Hash256 currentHash = parentHash;
        BlockHeader childHeader = inner.Get(currentHash) ?? throw new ArgumentException($"Parent {currentHash} is not found");
        headers.Add(decoder.Encode(childHeader).Bytes);

        if (_lowestRequestedHeader is not null)
        {
            for (long i = childHeader.Number - 1; i >= _lowestRequestedHeader.Value; i--)
            {
                currentHash = childHeader.ParentHash;
                childHeader = inner.Get(currentHash, i) ?? throw new ArgumentException($"Unable to get requested header at hash {currentHash} and number {i} during witness generation");
                headers.Add(decoder.Encode(childHeader).Bytes);
            }
        }

        return headers.ToArray();
    }
}
