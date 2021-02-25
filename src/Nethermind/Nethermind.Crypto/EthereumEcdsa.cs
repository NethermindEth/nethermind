﻿//  Copyright (c) 2021 Demerzel Solutions Limited
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
using System.Globalization;
using System.IO;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Secp256k1;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Crypto
{
    /// <summary>
    ///     for ecdsa tests
    ///     http://blog.enuma.io/update/2016/11/01/a-tale-of-two-curves-hardware-signing-for-ethereum.html
    /// </summary>
    public class EthereumEcdsa : Ecdsa, IEthereumEcdsa
    {
        public static readonly BigInteger MaxLowS =
            BigInteger.Parse("7FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF5D576E7357A4501DDFE92F46681B20A0",
                NumberStyles.HexNumber);

        public static readonly BigInteger LowSTransform =
            BigInteger.Parse("00FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364141",
                NumberStyles.HexNumber);

        private readonly ulong _chainIdValue;
        private readonly ILogger _logger;

        public EthereumEcdsa(ulong chainId, ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _chainIdValue = chainId;
        }

        public void Sign(PrivateKey privateKey, Transaction tx, bool isEip155Enabled)
        {
            if (_logger.IsDebug)
                _logger.Debug(
                    $"Signing transaction {tx.SenderAddress} -> {tx.To} ({tx.Value}) with data of length {tx.Data?.Length}");

            //Keccak hash = Keccak.Compute(Bytes.Concat((byte)tx.Type, Rlp.Encode(tx, true, isEip155Enabled, _chainIdValue).Bytes));
            
            Keccak hash = Keccak.Compute(Rlp.Encode(tx, true, isEip155Enabled, _chainIdValue).Bytes);
            tx.Signature = Sign(privateKey, hash);

            if (tx.Type != TxType.Legacy)
            {
                tx.ChainId = _chainIdValue;
            }

            if (tx.Type == TxType.Legacy && isEip155Enabled)
            {
                tx.Signature.V = tx.Signature.V + 8 + 2 * _chainIdValue;
            }

            if (_logger.IsDebug) _logger.Debug($"Transaction {tx.SenderAddress} -> {tx.To} ({tx.Value}) signed");
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="tx"></param>
        /// <returns></returns>
        public bool Verify(Address sender, Transaction tx)
        {
            Address? recovered = RecoverAddress(tx);
            return recovered?.Equals(sender) ?? false;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="tx"></param>
        /// <param name="useSignatureChainId"></param>
        /// <returns></returns>
        public Address? RecoverAddress(Transaction tx, bool useSignatureChainId = false)
        {
            if (tx.Signature == null)
            {
                throw new InvalidDataException("Cannot recover sender address from a transaction without a signature.");
            }

            useSignatureChainId &= tx.Signature.ChainId.HasValue;

            // feels like it is the same check twice
            bool applyEip155 = useSignatureChainId
                               || tx.Signature.V == _chainIdValue * 2 + 35ul
                               || tx.Signature.V == _chainIdValue * 2 + 36ul;

            ulong chainId;
            switch (tx.Type)
            {
                case TxType.Legacy when useSignatureChainId:
                    chainId = tx.Signature.ChainId.Value;
                    break;
                case TxType.Legacy:
                    chainId = _chainIdValue;
                    break;
                default:
                    chainId = tx.ChainId!.Value;
                    break;
            }

            Keccak hash = Keccak.Compute(Rlp.Encode(tx, true, applyEip155, chainId).Bytes);
            return RecoverAddress(tx.Signature, hash);
        }

        public Address? RecoverAddress(Signature signature, Keccak message)
        {
            return RecoverAddress(signature.BytesWithRecovery, message);
        }

        public Address? RecoverAddress(Span<byte> signatureBytes, Keccak message)
        {
            Span<byte> publicKey = stackalloc byte[65];
            bool success = Proxy.RecoverKeyFromCompact(
                publicKey,
                message.Bytes,
                signatureBytes.Slice(0, 64),
                signatureBytes[64],
                false);
            
            return !success ? null : PublicKey.ComputeAddress(publicKey.Slice(1, 64));
        }
    }
}
