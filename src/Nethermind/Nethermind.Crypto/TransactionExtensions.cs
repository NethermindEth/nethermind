// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Reflection.Metadata;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Merkleization;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Crypto
{
    public static class TransactionExtensions
    {
        private static readonly RlpTxDecoder<Transaction> _txDecoder = new();
        public static Keccak CalculateHash(this Transaction transaction)
        {
            switch(transaction.Type){
                case TxType.Blob:
                    Merkle.Ize(out Dirichlet.Numerics.UInt256 root, transaction);
                    byte[]? dataToHash = new byte[33];
                    dataToHash[0] = (byte)TxType.Blob;
                    root.ToLittleEndian(dataToHash.AsSpan(1));
                    byte[] hash = Keccak.Compute(dataToHash).Bytes;
                    return new Keccak(hash);
                default:
                    KeccakRlpStream stream = new();
                    _txDecoder.Encode(stream, transaction, RlpBehaviors.SkipTypedWrapping);
                    return stream.GetHash();
            }
        }
    }
}
