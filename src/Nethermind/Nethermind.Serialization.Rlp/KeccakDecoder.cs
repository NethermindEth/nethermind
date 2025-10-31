// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Serialization.Rlp
{
    public sealed class KeccakDecoder : RlpValueDecoder<Hash256>
    {
        public static readonly KeccakDecoder Instance = new();

        protected override Hash256? DecodeInternal(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None) => decoderContext.DecodeKeccak();

        protected override Hash256? DecodeInternal(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None) => rlpStream.DecodeKeccak();

        public override int GetLength(Hash256 item, RlpBehaviors rlpBehaviors) => Rlp.LengthOf(item);

        public override void Encode(RlpStream stream, Hash256 item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            stream.Encode(item);
        }
    }
}
