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

using System;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Secp256k1;

namespace Nethermind.Crypto
{
    [DoNotUseInSecuredContext("Any secure private key handling should be done on hardware or with memory protection")]
    public class PrivateKey : IDisposable
    {
        public byte[] KeyBytes { get; }

        private const int PrivateKeyLengthInBytes = 32;
        private PublicKey _publicKey;

        public PrivateKey(string hexString)
            : this(Bytes.FromHexString(hexString))
        {
        }

        public PrivateKey(byte[] keyBytes)
        {
            if (keyBytes is null)
            {
                throw new ArgumentNullException(nameof(keyBytes));
            }

            if (!Proxy.VerifyPrivateKey(keyBytes))
            {
                throw new ArgumentException("provided value is not a valid private key", nameof(keyBytes));
            }

            if (keyBytes.Length != PrivateKeyLengthInBytes)
            {
                throw new ArgumentException($"{nameof(PrivateKey)} should be {PrivateKeyLengthInBytes} bytes long",
                    nameof(keyBytes));
            }

            KeyBytes = new byte[32];
            keyBytes.AsSpan().CopyTo(KeyBytes);
        }

        public PublicKey PublicKey => _publicKey == null ? LazyInitializer.EnsureInitialized(ref _publicKey, ComputePublicKey) : _publicKey;

        public Address Address => PublicKey.Address;

        private bool Equals(PrivateKey other)
        {
            return Bytes.AreEqual(KeyBytes, other.KeyBytes);
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj))
            {
                return false;
            }

            if (ReferenceEquals(this, obj))
            {
                return true;
            }

            if (obj.GetType() != GetType())
            {
                return false;
            }

            return Equals((PrivateKey) obj);
        }

        public override int GetHashCode()
        {
            return MemoryMarshal.Read<int>(KeyBytes);
        }

        private PublicKey ComputePublicKey()
        {
            return new(Proxy.GetPublicKey(KeyBytes, false));
        }

        public override string ToString()
        {
            return KeyBytes.ToHexString(true);
        }

        public void Dispose()
        {
            for (int i = 0; i < KeyBytes?.Length; i++)
            {
                KeyBytes[i] = 0;
            }
        }
    }
}
