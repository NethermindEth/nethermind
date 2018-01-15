/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Threading;

namespace Nevermind.Core.Crypto
{
    public class PrivateKey
    {
        private const int PrivateKeyLengthInBytes = 32;
        private PublicKey _publicKey;

        public PrivateKey()
            :this(Random.GeneratePrivateKey())
        {
        }

        public PrivateKey(Hex key)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            if (key.ByteLength != PrivateKeyLengthInBytes)
            {
                throw new ArgumentException($"{nameof(PrivateKey)} should be {PrivateKeyLengthInBytes} bytes long", nameof(key));
            }

            Hex = key;
        }

        public Hex Hex { get; }

        private PublicKey ComputePublicKey()
        {
            return new PublicKey(Secp256k1.Proxy.Proxy.GetPublicKey(Hex, false));
        }

        public PublicKey PublicKey => LazyInitializer.EnsureInitialized(ref _publicKey, ComputePublicKey);

        public Address Address => PublicKey.Address;

        public override string ToString()
        {
            return Hex.ToString(true);
        }
    }
}