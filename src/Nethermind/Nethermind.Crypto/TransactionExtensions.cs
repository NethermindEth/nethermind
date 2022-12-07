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
        private static readonly TxDecoder<Transaction> _txDecoder = new();

        public static Keccak CalculateHash(this Transaction transaction)
        {
            KeccakRlpStream stream = new();
            _txDecoder.Encode(stream, transaction, RlpBehaviors.SkipTypedWrapping);
            return stream.GetHash();
        }
    }
}
