// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Merge.AuRa.Shutter.Contracts;

public interface ISequencerContract
{
    public IEnumerable<TransactionSubmitted> GetEvents();

    struct TransactionSubmitted
    {

        public ulong Eon;
        public Bytes32 IdentityPrefix;
        public Address Sender;
        public byte[] EncryptedTransaction;
        public UInt256 GasLimit;
    }
}
