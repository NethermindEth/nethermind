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
using System.Globalization;
using System.Numerics;
using Nethermind.Core.Encoding;
using Nethermind.Core.Specs;
using Nethermind.Logging;

namespace Nethermind.Core.Crypto
{
    /// <summary>
    ///     for ecdsa tests
    ///     http://blog.enuma.io/update/2016/11/01/a-tale-of-two-curves-hardware-signing-for-ethereum.html
    /// </summary>
    public class EthereumEcdsa : Ecdsa, IEthereumEcdsa
    {
        public static readonly BigInteger MaxLowS = BigInteger.Parse("7FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF5D576E7357A4501DDFE92F46681B20A0", NumberStyles.HexNumber);
        public static readonly BigInteger LowSTransform = BigInteger.Parse("00FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364141", NumberStyles.HexNumber);

        private readonly int _chainIdValue;
        private readonly ISpecProvider _specProvider;
        private readonly ILogger _logger;

        public EthereumEcdsa(ISpecProvider specProvider, ILogManager logManager)
        {
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _chainIdValue = specProvider.ChainId;
        }

        public void Sign(PrivateKey privateKey, Transaction tx, long blockNumber)
        {
            if(_logger.IsDebug) _logger.Debug($"Signing transaction {tx.SenderAddress} -> {tx.To} ({tx.Value}) with data {tx.Data}");
            bool isEip155Enabled = _specProvider.GetSpec(blockNumber).IsEip155Enabled;
            Keccak hash = Keccak.Compute(Rlp.Encode(tx, true, isEip155Enabled, _chainIdValue));
            tx.Signature = Sign(privateKey, hash);
            if (isEip155Enabled)
            {
                tx.Signature.V = tx.Signature.V + 8 + 2 * _chainIdValue;
            }

            if(_logger.IsDebug) _logger.Debug($"Transaction {tx.SenderAddress} -> {tx.To} ({tx.Value}) signed");
        }

        public bool Verify(Address sender, Transaction tx, long blockNumber)
        {
            bool isEip155Enabled = _specProvider.GetSpec(blockNumber).IsEip155Enabled;
            Keccak hash = Keccak.Compute(Rlp.Encode(tx, true, isEip155Enabled, _chainIdValue));
            Address recovered = RecoverAddress(tx.Signature, hash);
            return recovered.Equals(sender);
        }

        public Address RecoverAddress(Transaction tx, long blockNumber)
        {
            bool isEip155Enabled = _specProvider.GetSpec(blockNumber).IsEip155Enabled;
            bool applyEip155 = isEip155Enabled && (tx.Signature.V == _chainIdValue * 2 + 35 || tx.Signature.V == _chainIdValue * 2 + 36);
            Keccak hash = Keccak.Compute(Rlp.Encode(tx, true, applyEip155, _chainIdValue));
            return RecoverAddress(tx.Signature, hash);
        }

        public Address RecoverAddress(Signature signature, Keccak message)
        {
            return RecoverPublicKey(signature, message)?.Address;
        }
    }
}