// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Buffers;

namespace Nethermind.Serialization.Rlp
{
    public static class ByteArrayExtensions
    {
        public static RlpStream AsRlpStream(this byte[]? bytes) => new(bytes ?? []);

        public static RlpStream AsRlpStream(in this CappedArray<byte> bytes) => new(in bytes.IsNotNull ? ref bytes : ref CappedArray<byte>.Empty);



        public static Rlp.ValueDecoderContext AsRlpValueContext(this byte[]? bytes) => new(bytes ?? []);

        public static Rlp.ValueDecoderContext AsRlpValueContext(this Span<byte> span) => ((ReadOnlySpan<byte>)span).AsRlpValueContext();

        public static Rlp.ValueDecoderContext AsRlpValueContext(this ReadOnlySpan<byte> span) => span.IsEmpty ? new Rlp.ValueDecoderContext([]) : new Rlp.ValueDecoderContext(span);
    }
}
