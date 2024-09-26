// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Crypto
{
    public static class TransactionExtensions
    {
        public static Hash256 CalculateHash(this Transaction transaction)
        {
            KeccakRlpStream stream = new();
            TxDecoder.Instance.Encode(stream, transaction, RlpBehaviors.SkipTypedWrapping);
            return stream.GetHash();
        }
    }
}
