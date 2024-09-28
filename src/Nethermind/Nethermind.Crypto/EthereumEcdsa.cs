// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
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

        private readonly AuthorizationTupleDecoder _tupleDecoder = AuthorizationTupleDecoder.Instance;
        private readonly ulong _chainIdValue;
        public ulong ChainId => _chainIdValue;

        public EthereumEcdsa(ulong chainId)
        {
            _chainIdValue = chainId;
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
                throw new InvalidDataException("Cannot recover sender address from a transaction without a signature.");
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
            Hash256 hash = Keccak.Compute(Rlp.Encode(tx, true, applyEip155, chainId).Bytes);

            return RecoverAddress(tx.Signature, hash);
        }

        [SkipLocalsInit]
        public Address? RecoverAddress(AuthorizationTuple tuple)
        {
            Span<byte> buffer = stackalloc byte[128];
            buffer[0] = Eip7702Constants.Magic;
            using NettyRlpStream stream = _tupleDecoder.EncodeWithoutSignature(tuple.ChainId, tuple.CodeAddress, tuple.Nonce);
            stream.AsSpan().CopyTo(buffer.Slice(1));
            return RecoverAddress(tuple.AuthoritySignature, Keccak.Compute(buffer.Slice(0, stream.Length + 1)));
        }

        public static ulong CalculateV(ulong chainId, bool addParity = true) => chainId * 2 + 35ul + (addParity ? 1u : 0u);

        public Address? RecoverAddress(Signature signature, Hash256 message)
        {
            return RecoverAddress(signature.BytesWithRecovery, message);
        }

        public Address? RecoverAddress(Span<byte> signatureBytes, Hash256 message)
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

    public static class EthereumEcdsaExtension
    {

        public static AuthorizationTuple Sign(this IEthereumEcdsa ecdsa, PrivateKey signer, ulong chainId, Address codeAddress, ulong nonce)
        {
            using NettyRlpStream rlp = AuthorizationTupleDecoder.Instance.EncodeWithoutSignature(chainId, codeAddress, nonce);
            Span<byte> preImage = stackalloc byte[rlp.Length + 1];
            preImage[0] = Eip7702Constants.Magic;
            rlp.AsSpan().CopyTo(preImage.Slice(1));
            Signature sig = ecdsa.Sign(signer, Keccak.Compute(preImage));
            return new AuthorizationTuple(chainId, codeAddress, nonce, sig);
        }

        public static void Sign(this IEthereumEcdsa ecdsa, PrivateKey privateKey, Transaction tx, bool isEip155Enabled = true)
        {
            if (tx.Type != TxType.Legacy)
            {
                tx.ChainId = ecdsa.ChainId;
            }

            Hash256 hash = Keccak.Compute(Rlp.Encode(tx, true, isEip155Enabled, ecdsa.ChainId).Bytes);
            tx.Signature = ecdsa.Sign(privateKey, hash);

            if (tx.Type == TxType.Legacy && isEip155Enabled)
            {
                tx.Signature.V = tx.Signature.V + 8 + 2 * ecdsa.ChainId;
            }
        }
    }
}
