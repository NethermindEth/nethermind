// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Serialization.Rlp
{
    public sealed class NonNullDecoderWrapper<T>(IRlpValueDecoder<T?> inner) : IRlpValueDecoder<T> where T : class
    {
        public T Decode(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
            inner.Decode(ref decoderContext, rlpBehaviors) ?? throw new RlpException($"Decoded {typeof(T).Name} is null");

        public int GetLength(T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None) => inner.GetLength(item, rlpBehaviors);
    }
}
