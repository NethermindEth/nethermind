// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Crypto
{
    public static class TransactionExtensions
    {
        public static Keccak CalculateHash(this Transaction transaction) => Keccak.Compute(Rlp.Encode(transaction).Bytes);
    }
}
