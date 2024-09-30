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
}
