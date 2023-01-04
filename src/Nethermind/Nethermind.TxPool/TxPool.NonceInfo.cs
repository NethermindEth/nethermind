// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.TxPool
{
    public partial class TxPool
    {
        internal class NonceInfo
        {
            public UInt256 Value { get; }

            public Keccak? TransactionHash { get; private set; }

            public NonceInfo(in UInt256 value)
            {
                Value = value;
            }

            public void SetTransactionHash(Keccak transactionHash)
            {
                TransactionHash = transactionHash;
            }
        }
    }
}
