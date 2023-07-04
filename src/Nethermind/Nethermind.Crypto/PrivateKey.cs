// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Crypto
{
    [DoNotUseInSecuredContext("Any secure private key handling should be done on hardware or with memory protection")]
    public class PrivateKey : IDisposable
    {
        public byte[] KeyBytes { get; }

        private const int PrivateKeyLengthInBytes = 32;
        private PublicKey? _publicKey;
        private CompressedPublicKey? _compressedPublicKey;

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

            if (!SecP256k1.VerifyPrivateKey(keyBytes))
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

        public PublicKey PublicKey =>
            _publicKey ?? LazyInitializer.EnsureInitialized(ref _publicKey, ComputePublicKey);

        public CompressedPublicKey CompressedPublicKey =>
            _compressedPublicKey ?? LazyInitializer.EnsureInitialized(ref _compressedPublicKey, ComputeCompressedPublicKey);

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

            return Equals((PrivateKey)obj);
        }

        public override int GetHashCode()
        {
            return MemoryMarshal.Read<int>(KeyBytes);
        }

        private PublicKey ComputePublicKey()
        {
            return new(SecP256k1.GetPublicKey(KeyBytes, false));
        }

        private CompressedPublicKey ComputeCompressedPublicKey()
        {
            return new(SecP256k1.GetPublicKey(KeyBytes, true));
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
