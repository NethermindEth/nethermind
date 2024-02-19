// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Buffers;

namespace Nethermind.Serialization.Rlp
{
    public static class ByteArrayExtensions
    {
        public static RlpStream AsRlpStream(this byte[]? bytes)
        {
            return new(bytes ?? Array.Empty<byte>());
        }

        public static RlpStream AsRlpStream(in this CappedArray<byte> bytes)
        {
            return new(in bytes.IsNotNull ? ref bytes : ref CappedArray<byte>.Empty);
        }

        public static RlpFactory AsRlpFactory(this byte[]? bytes)
        {
            return new(bytes ?? Array.Empty<byte>());
        }

        public static RlpFactory AsRlpFactory(in this CappedArray<byte> bytes)
        {
            return new(in bytes.IsNotNull ? ref bytes : ref CappedArray<byte>.Empty);
        }

        public static Rlp.ValueDecoderContext AsRlpValueContext(this byte[]? bytes)
        {
            return new(bytes ?? Array.Empty<byte>());
        }

        public static Rlp.ValueDecoderContext AsRlpValueContext(this Span<byte> span)
        {
            return ((ReadOnlySpan<byte>)span).AsRlpValueContext();
        }

        public static Rlp.ValueDecoderContext AsRlpValueContext(this ReadOnlySpan<byte> span)
        {
            return span.IsEmpty ? new Rlp.ValueDecoderContext(Array.Empty<byte>()) : new Rlp.ValueDecoderContext(span);
        }
    }
}
