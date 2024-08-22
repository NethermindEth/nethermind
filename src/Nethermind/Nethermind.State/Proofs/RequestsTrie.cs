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

public class RequestsTrie : PatriciaTrie<ConsensusRequest>
{
    private static readonly ConsensusRequestDecoder _codec = new();

    public RequestsTrie(ConsensusRequest[]? requests, bool canBuildProof = false, ICappedArrayPool? bufferPool = null)
        : base(requests, canBuildProof, bufferPool)
    {
    }

    protected override void Initialize(ConsensusRequest[] requests)
    {
        var key = 0;

        foreach (ConsensusRequest exit in requests)
        {
            Set(Rlp.Encode(key++).Bytes, _codec.Encode(exit, RlpBehaviors.SkipTypedWrapping).Bytes);
        }
    }

    public static Hash256 CalculateRoot(ConsensusRequest[] requests)
    {
        using TrackingCappedArrayPool cappedArray = new(requests.Length * 4);
        Hash256 rootHash = new RequestsTrie(requests, canBuildProof: false, bufferPool: cappedArray).RootHash;
        return rootHash;
    }
}
