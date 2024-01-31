// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.ValidatorExit;

namespace Nethermind.Serialization.Rlp;

public class ValidatorExitsDecoder : IRlpStreamDecoder<ValidatorExit>, IRlpValueDecoder<ValidatorExit>
{
    public int GetLength(ValidatorExit item, RlpBehaviors rlpBehaviors) =>
        Rlp.LengthOfSequence(Rlp.LengthOf(item.SourceAddress) + Rlp.LengthOf(item.ValidatorPubkey));

    public ValidatorExit Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        throw new System.NotImplementedException();
    }

    public void Encode(RlpStream stream, ValidatorExit item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        throw new System.NotImplementedException();
    }

    public ValidatorExit Decode(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        throw new System.NotImplementedException();
    }
}
