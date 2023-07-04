// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Serialization.Rlp
{
    public static class ByteArrayExtensions
    {
        public static RlpStream AsRlpStream(this byte[]? bytes)
        {
            return new(bytes ?? Array.Empty<byte>());
        }

        public static Rlp.ValueDecoderContext AsRlpValueContext(this byte[]? bytes)
        {
            return new(bytes ?? Array.Empty<byte>());
        }

        public static Rlp.ValueDecoderContext AsRlpValueContext(this Span<byte> span)
        {
            return span.IsEmpty ? new Rlp.ValueDecoderContext(Array.Empty<byte>()) : new Rlp.ValueDecoderContext(span);
        }
    }
}
