// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;

namespace Nethermind.Core.Crypto
{

    public class CompressedPublicKey : IEquatable<CompressedPublicKey>
    {
        public const int LengthInBytes = 33;

        public CompressedPublicKey(string? hexString)
            : this(Core.Extensions.Bytes.FromHexString(hexString ?? throw new ArgumentNullException(nameof(hexString))))
        {
        }

        public CompressedPublicKey(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length != LengthInBytes)
            {
                throw new ArgumentException($"{nameof(CompressedPublicKey)} should be {LengthInBytes} bytes long",
                    nameof(bytes));
            }

            Bytes = bytes.Slice(bytes.Length - LengthInBytes, LengthInBytes).ToArray();
        }

        public PublicKey Decompress()
        {
            return new PublicKey(SecP256k1.Decompress(Bytes));
        }

        public byte[] Bytes { get; }

        public bool Equals(CompressedPublicKey? other)
        {
            if (other is null)
            {
                return false;
            }

            return Core.Extensions.Bytes.AreEqual(Bytes, other.Bytes);
        }

        public override bool Equals(object? obj)
        {
            return Equals(obj as CompressedPublicKey);
        }

        public override int GetHashCode()
        {
            return MemoryMarshal.Read<int>(Bytes);
        }

        public override string ToString()
        {
            return Bytes.ToHexString(true);
        }

        public string ToString(bool with0X)
        {
            return Bytes.ToHexString(with0X);
        }
    }
}
