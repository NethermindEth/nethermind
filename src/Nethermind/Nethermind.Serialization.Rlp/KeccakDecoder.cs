// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Serialization.Rlp
{
    [method: DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(KeccakDecoder))]
    public sealed class KeccakDecoder() : RlpDecoder<Hash256>
    {
        public static readonly KeccakDecoder Instance = new();

        protected override Hash256? DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None) => decoderContext.DecodeKeccak();

        public override int GetLength(Hash256 item, RlpBehaviors rlpBehaviors) => Rlp.LengthOf(item);

        public override void Encode<TWriter>(ref TWriter writer, Hash256 item, RlpBehaviors rlpBehaviors = RlpBehaviors.None) => writer.Encode(item);
    }
}
