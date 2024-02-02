// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.ValidatorExit;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Trie;
using Nethermind.Trie;

namespace Nethermind.State.Proofs;

public class ValidatorExitsTrie : PatriciaTrie<ValidatorExit>
{
    private static readonly ValidatorExitsDecoder _codec = new();

    public ValidatorExitsTrie(ValidatorExit[]? validatorExits, bool canBuildProof, ICappedArrayPool? bufferPool = null)
        : base(validatorExits, canBuildProof, bufferPool)
    {
        ArgumentNullException.ThrowIfNull(validatorExits);
    }

    protected override void Initialize(ValidatorExit[] validatorExits)
    {
        var key = 0;

        foreach (ValidatorExit exit in validatorExits)
        {
            Set(Rlp.Encode(key++).Bytes, _codec.Encode(exit).Bytes);
        }
    }

    public static Hash256 CalculateRoot(ValidatorExit[] validatorExits)
    {
        using TrackingCappedArrayPool cappedArray = new(validatorExits.Length * 4);
        Hash256 rootHash = new ValidatorExitsTrie(validatorExits, canBuildProof: false, bufferPool: cappedArray).RootHash;
        return rootHash;
    }
}
