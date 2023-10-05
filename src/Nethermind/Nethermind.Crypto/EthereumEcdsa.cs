// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Globalization;
using System.IO;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
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
            if (tx.Signature is null)
            {
                // TODO: Optimism. No signature in system transactions
                return Address.Zero;
            }

            useSignatureChainId &= tx.Signature.ChainId.HasValue;

            // feels like it is the same check twice
            bool applyEip155 = useSignatureChainId
                               || tx.Signature.V == CalculateV(_chainIdValue, false)
                               || tx.Signature.V == CalculateV(_chainIdValue, true);

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

        public static ulong CalculateV(ulong chainId, bool addParity = true) => chainId * 2 + 35ul + (addParity ? 1u : 0u);

        public Address? RecoverAddress(Signature signature, Keccak message)
        {
            return RecoverAddress(signature.BytesWithRecovery, message);
        }

        public Address? RecoverAddress(Span<byte> signatureBytes, Keccak message)
        {
            Span<byte> publicKey = stackalloc byte[65];
            bool success = SpanSecP256k1.RecoverKeyFromCompact(
                publicKey,
                message.Bytes,
                signatureBytes[..64],
                signatureBytes[64],
                false);

            return !success ? null : PublicKey.ComputeAddress(publicKey.Slice(1, 64));
        }
    }
}
