// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using System;

namespace Nethermind.Crypto
{
    public static class TransactionExtensions
    {
        private static readonly Lazy<TxDecoder> _txDecoder = new(() => TxDecoder.Instance);
        public static Hash256 CalculateHash(this Transaction transaction)
        {
            KeccakRlpStream stream = new();
            _txDecoder.Value.Encode(stream, transaction, RlpBehaviors.SkipTypedWrapping);
            return stream.GetHash();
        }
    }
}
