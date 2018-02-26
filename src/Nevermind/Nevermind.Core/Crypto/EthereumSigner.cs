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
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Nevermind.Core.Encoding;
using Nevermind.Core.Potocol;
using Secp256k1.Proxy;

namespace Nevermind.Core.Crypto
{
    /// <summary>
    ///     for signer tests
    ///     http://blog.enuma.io/update/2016/11/01/a-tale-of-two-curves-hardware-signing-for-ethereum.html
    /// </summary>
    public class EthereumSigner : Signer, IEthereumSigner
    {
        public static readonly BigInteger MaxLowS = BigInteger.Parse("7FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF5D576E7357A4501DDFE92F46681B20A0", NumberStyles.HexNumber);
        public static readonly BigInteger LowSTransform = BigInteger.Parse("00FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364141", NumberStyles.HexNumber);

        private readonly int _chainIdValue;
        private readonly IEthereumRelease _ethereumRelease;

        public EthereumSigner(IEthereumRelease ethereumRelease, int chainIdValue)
        {
            _ethereumRelease = ethereumRelease;
            _chainIdValue = chainIdValue;
        }

        public EthereumSigner(IEthereumRelease ethereumRelease, ChainId chainId)
            : this(ethereumRelease, (int)chainId)
        {
        }

        public void Sign(PrivateKey privateKey, Transaction transaction)
        {
            Keccak hash = Keccak.Compute(Rlp.Encode(transaction, true, _ethereumRelease.IsEip155Enabled, _chainIdValue));
            transaction.Signature = Sign(privateKey, hash);
        }

        public bool Verify(Address sender, Transaction transaction)
        {
            Keccak hash = Keccak.Compute(Rlp.Encode(transaction, true, _ethereumRelease.IsEip155Enabled, _chainIdValue));
            Address recovered = Recover(transaction.Signature, hash);
            return recovered.Equals(sender);
        }

        public Address Recover(Transaction transaction)
        {
            Keccak hash = Keccak.Compute(Rlp.Encode(transaction, true));
            return Recover(transaction.Signature, hash);
        }

        public Address Recover(Signature signature, Keccak message)
        {
            return RecoverPublicKey(signature, message).Address;
        }
    }
}