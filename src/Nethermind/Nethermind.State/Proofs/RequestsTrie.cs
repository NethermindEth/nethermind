// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Buffers;
using Nethermind.Core.ConsensusRequests;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Trie;
using Nethermind.Trie;

namespace Nethermind.State.Proofs;

public class RequestsTrie(ConsensusRequest[]? requests, bool canBuildProof = false, ICappedArrayPool? bufferPool = null)
    : PatriciaTrie<ConsensusRequest>(requests, canBuildProof, bufferPool)
{
    private static readonly ConsensusRequestDecoder _codec = ConsensusRequestDecoder.Instance;

    protected override void Initialize(ConsensusRequest[] requests)
    {
        var key = 0;

        foreach (ConsensusRequest req in requests)
        {
            Set(Rlp.Encode(key++).Bytes, _codec.Encode(req, RlpBehaviors.SkipTypedWrapping).Bytes);
        }
    }

    public static Hash256 CalculateRoot(ConsensusRequest[] requests)
    {
        using TrackingCappedArrayPool cappedArray = new(requests.Length * 4);
        Hash256 rootHash = new RequestsTrie(requests, canBuildProof: false, bufferPool: cappedArray).RootHash;
        return rootHash;
    }
}


public static class ConsensusRequestExtensions
{
    public static Hash256 CalculateRootHash(this ConsensusRequest[]? requests)
    {
        Rlp[] encodedRequests = new Rlp[requests!.Length];
        for (int i = 0; i < encodedRequests.Length; i++)
        {
            encodedRequests[i] = Rlp.Encode(requests![i].Encode());
        }

        return Keccak.Compute(Rlp.Encode(encodedRequests).Bytes);
    }
}
