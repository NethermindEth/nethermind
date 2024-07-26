// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Serialization.Rlp
{
    public class KeccakDecoder : IRlpValueDecoder<Hash256>
    {
        public static readonly KeccakDecoder Instance = new();

        public Hash256? Decode(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None) => decoderContext.DecodeKeccak();

        public static Rlp Encode(Hash256 item, RlpBehaviors rlpBehaviors = RlpBehaviors.None) => Rlp.Encode(item);

        public int GetLength(Hash256 item, RlpBehaviors rlpBehaviors) => Rlp.LengthOf(item);
    }
}
