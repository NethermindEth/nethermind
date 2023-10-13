// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Serialization.Rlp
{
    public class CommitmentDecoder : IRlpValueDecoder<Commitment>
    {
        public static readonly CommitmentDecoder Instance = new();

        public Commitment? Decode(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None) => decoderContext.DecodeKeccak();

        public Rlp Encode(Commitment item, RlpBehaviors rlpBehaviors = RlpBehaviors.None) => Rlp.Encode(item);

        public int GetLength(Commitment item, RlpBehaviors rlpBehaviors) => Rlp.LengthOf(item);
    }
}
