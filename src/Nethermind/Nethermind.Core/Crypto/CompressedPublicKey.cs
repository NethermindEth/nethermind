//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;
using System.Runtime.InteropServices;
using Nethermind.Core.Extensions;
using Nethermind.Secp256k1;

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
        return new PublicKey(Proxy.Decompress(Bytes));
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
