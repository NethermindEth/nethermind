// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.ValidatorExit;

namespace Nethermind.Serialization.Rlp;

public class ValidatorExitsDecoder : IRlpObjectDecoder<ValidatorExit>
{
    public int GetLength(ValidatorExit item, RlpBehaviors rlpBehaviors)
    {
        throw new System.NotImplementedException();
    }

    public Rlp Encode(ValidatorExit item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        throw new System.NotImplementedException();
    }
}
