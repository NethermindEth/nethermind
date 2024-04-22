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

public class ValidatorExitsTrie : PatriciaTrie<WithdrawalRequest>
{
    private static readonly ValidatorExitsDecoder _codec = new();

    public ValidatorExitsTrie(WithdrawalRequest[]? validatorExits, bool canBuildProof, ICappedArrayPool? bufferPool = null)
        : base(validatorExits, canBuildProof, bufferPool)
    {
        ArgumentNullException.ThrowIfNull(validatorExits);
    }

    protected override void Initialize(WithdrawalRequest[] validatorExits)
    {
        var key = 0;

        foreach (WithdrawalRequest exit in validatorExits)
        {
            Set(Rlp.Encode(key++).Bytes, _codec.Encode(exit).Bytes);
        }
    }

    public static Hash256 CalculateRoot(WithdrawalRequest[] validatorExits)
    {
        using TrackingCappedArrayPool cappedArray = new(validatorExits.Length * 4);
        Hash256 rootHash = new ValidatorExitsTrie(validatorExits, canBuildProof: false, bufferPool: cappedArray).RootHash;
        return rootHash;
    }
}
