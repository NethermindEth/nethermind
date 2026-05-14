// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Serialization.Rlp;

public abstract class RlpDecoder<T> : IRlpDecoder<T>
{
    public abstract int GetLength(T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

    public abstract void Encode(RlpStream stream, T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

    [DoesNotReturn]
    [StackTraceHidden]
    protected static void ThrowRlpException(Exception exception) =>
        throw new RlpException($"Cannot decode stream of {nameof(T)}", exception);

    public T Decode(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        try
        {
            return DecodeInternal(ref decoderContext, rlpBehaviors);
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            ThrowRlpException(e);
            return default;
        }
    }

    protected abstract T DecodeInternal(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None);
}
