// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Serialization.Rlp
{
    [method: DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(KeccakDecoder))]
    public sealed class KeccakDecoder() : RlpDecoder<Hash256>
    {
        public static readonly KeccakDecoder Instance = new();

        protected override Hash256? DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None) => decoderContext.DecodeKeccakOrNull();

        public override void PopulateLength(Hash256? item, ref LengthStack lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None) => lengths.Push(Rlp.LengthOf(item));

        public override void Encode<TWriter>(Hash256? item, ref LengthStack lengths, ref TWriter writer, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            lengths.Pop();
            writer.Encode(item);
        }

        public override Rlp Encode(Hash256? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None) => Rlp.Encode(item);

        public override CappedArray<byte> EncodeToCappedArray(Hash256? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None, ICappedArrayPool? bufferPool = null)
        {
            int size = Rlp.LengthOf(item);
            CappedArray<byte> buffer = bufferPool.SafeRent(size);
            RlpWriter writer = new(in buffer);
            writer.Encode(item);
            return buffer;
        }
    }
}
