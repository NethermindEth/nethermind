// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Shutter.Contracts;

public interface ISequencerContract
{
    public IEnumerable<TransactionSubmitted> GetEvents();

    struct TransactionSubmitted
    {
        public ulong Eon;
        public ulong TxIndex;
        public Bytes32 IdentityPrefix;
        public Address Sender;
        public byte[] EncryptedTransaction;
        public UInt256 GasLimit;

        public readonly bool Equals(TransactionSubmitted o)
        {
            return Eon == o.Eon
                && TxIndex == o.TxIndex
                && IdentityPrefix.Equals(o.IdentityPrefix)
                && Sender.Equals(o.Sender)
                && Enumerable.SequenceEqual(EncryptedTransaction, o.EncryptedTransaction)
                && GasLimit.Equals(GasLimit);
        }
    }
}
