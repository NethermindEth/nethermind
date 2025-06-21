// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Crypto
{
    public static class EthereumEcdsaExtensions
    {
        private static readonly TxDecoder _txDecoder = TxDecoder.Instance;
        public static AuthorizationTuple Sign(this IEthereumEcdsa ecdsa, PrivateKey signer, ulong chainId, Address codeAddress, ulong nonce)
        {
            using NettyRlpStream rlp = AuthorizationTupleDecoder.Instance.EncodeWithoutSignature(chainId, codeAddress, nonce);
            Span<byte> preImage = stackalloc byte[rlp.Length + 1];
            preImage[0] = Eip7702Constants.Magic;
            rlp.AsSpan().CopyTo(preImage[1..]);
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

        /// <summary>
        ///
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="tx"></param>
        /// <returns></returns>
        public static bool Verify(this IEthereumEcdsa ecdsa, Address sender, Transaction tx)
        {
            Address? recovered = ecdsa.RecoverAddress(tx);
            return recovered?.Equals(sender) ?? false;
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="tx"></param>
        /// <param name="useSignatureChainId"></param>
        /// <returns></returns>
        public static Address? RecoverAddress(this IEthereumEcdsa ecdsa, Transaction tx, bool useSignatureChainId = false)
        {
            if (tx.Signature is null)
            {
                throw new InvalidDataException("Cannot recover sender address from a transaction without a signature.");
            }

            useSignatureChainId &= tx.Signature.ChainId.HasValue;

            // feels like it is the same check twice
            bool applyEip155 = useSignatureChainId
                               || tx.Signature.V == CalculateV(ecdsa.ChainId, false)
                               || tx.Signature.V == CalculateV(ecdsa.ChainId, true);
            var chainId = tx.Type switch
            {
                TxType.Legacy when useSignatureChainId => tx.Signature.ChainId.Value,
                TxType.Legacy => ecdsa.ChainId,
                _ => tx.ChainId!.Value,
            };

            KeccakRlpStream stream = new();
            _txDecoder.EncodeTx(stream, tx, RlpBehaviors.SkipTypedWrapping, true, applyEip155, chainId);

            return ecdsa.RecoverAddress(tx.Signature, stream.GetValueHash());
        }

        public static ulong CalculateV(ulong chainId, bool addParity = true) => chainId * 2 + 35ul + (addParity ? 1u : 0u);

        [SkipLocalsInit]
        public static Address? RecoverAddress(this IEthereumEcdsa ecdsa, AuthorizationTuple tuple)
        {
            Span<byte> buffer = stackalloc byte[128];
            buffer[0] = Eip7702Constants.Magic;
            using NettyRlpStream stream = AuthorizationTupleDecoder.Instance.EncodeWithoutSignature(tuple.ChainId, tuple.CodeAddress, tuple.Nonce);
            stream.AsSpan().CopyTo(buffer[1..]);
            return ecdsa.RecoverAddress(tuple.AuthoritySignature, Keccak.Compute(buffer[..(stream.Length + 1)]));
        }
    }
}
