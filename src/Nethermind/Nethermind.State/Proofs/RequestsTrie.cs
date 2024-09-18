// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Buffers;
using Nethermind.Core.ConsensusRequests;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;

namespace Nethermind.State.Proofs;

public class RequestsTrie(ConsensusRequest[]? requests, ICappedArrayPool bufferPool, bool canBuildProof = false)
    : PatriciaTrie<ConsensusRequest>(requests, new ConsensusRequestDecoder(), bufferPool, canBuildProof)
{
    public static Hash256 CalculateRoot(ConsensusRequest[] requests)
    {
        using TrackingCappedArrayPool cappedArray = new(requests.Length * 4);
        return new RequestsTrie(requests, canBuildProof: false, bufferPool: cappedArray).RootHash;
    }
}
