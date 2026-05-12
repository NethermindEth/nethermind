// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Text;

namespace Nethermind.Core.Crypto
{
    public static class Eip191Hasher
    {
        private static ReadOnlySpan<byte> Header => "Ethereum Signed Message:\n"u8;

        // EIP-191: keccak256("\x19Ethereum Signed Message:\n" || ascii(byteLength) || messageBytes).
        // Operates on raw bytes — never round-trip through UTF-8, since the message may not be valid UTF-8.
        public static Hash256 HashMessage(ReadOnlySpan<byte> message)
        {
            Span<byte> lengthDigits = stackalloc byte[20];
            Utf8Formatter.TryFormat(message.Length, lengthDigits, out int lengthByteCount);

            byte[] buffer = new byte[Header.Length + lengthByteCount + message.Length];
            Span<byte> span = buffer;
            Header.CopyTo(span);
            lengthDigits[..lengthByteCount].CopyTo(span[Header.Length..]);
            message.CopyTo(span[(Header.Length + lengthByteCount)..]);
            return Keccak.Compute(buffer);
        }
    }
}
