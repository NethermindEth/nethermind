// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Text;
using Nethermind.Core.Buffers;

namespace Nethermind.Core.Crypto
{
    public static class Eip191Hasher
    {
        // EIP-191 version byte (0x19) followed by the personal_sign domain separator.
        private static readonly byte[] HeaderBytes = [0x19, .. "Ethereum Signed Message:\n"u8];
        private static ReadOnlySpan<byte> Header => HeaderBytes;

        // EIP-191: keccak256("\x19Ethereum Signed Message:\n" || ascii(byteLength) || messageBytes).
        // Operates on raw bytes — never round-trip through UTF-8, since the message may not be valid UTF-8.
        public static Hash256 HashMessage(ReadOnlySpan<byte> message) => new(HashMessageValue(message));

        public static ValueHash256 HashMessageValue(ReadOnlySpan<byte> message)
        {
            Span<byte> lengthDigits = stackalloc byte[20];
            Utf8Formatter.TryFormat(message.Length, lengthDigits, out int lengthByteCount);

            int totalLength = Header.Length + lengthByteCount + message.Length;
            using ArrayPoolDisposableReturn _ = ArrayPoolDisposableReturn.Rent(totalLength, out byte[] buffer);
            Span<byte> span = buffer.AsSpan(0, totalLength);
            Header.CopyTo(span);
            lengthDigits[..lengthByteCount].CopyTo(span[Header.Length..]);
            message.CopyTo(span[(Header.Length + lengthByteCount)..]);
            return ValueKeccak.Compute(span);
        }
    }
}
