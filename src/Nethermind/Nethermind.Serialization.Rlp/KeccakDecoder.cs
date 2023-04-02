// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Serialization.Rlp
{
    public class KeccakDecoder : IRlpValueDecoder<Keccak>
    {
        public static readonly KeccakDecoder Instance = new();

        public Keccak? Decode(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None) => decoderContext.DecodeKeccak();

        public Rlp Encode(Keccak item, RlpBehaviors rlpBehaviors = RlpBehaviors.None) => Rlp.Encode(item);

        public int GetLength(Keccak item, RlpBehaviors rlpBehaviors) => Rlp.LengthOf(item);
    }
    public class ValueKeccakDecoder : IRlpValueDecoder<ValueKeccak>
    {
        public static readonly ValueKeccakDecoder Instance = new();

        public ValueKeccak Decode(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None) => decoderContext.DecodeValueKeccak();

        public Rlp Encode(ValueKeccak item, RlpBehaviors rlpBehaviors = RlpBehaviors.None) => Rlp.Encode(item);

        public int GetLength(ValueKeccak item, RlpBehaviors rlpBehaviors) => Rlp.LengthOf(item);
    }
}
