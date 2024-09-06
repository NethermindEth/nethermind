// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Shutter.Contracts;

public interface ISequencerContract
{
    public AbiEncodingInfo TransactionSubmittedAbi { get; }

    public struct TransactionSubmitted
    {
        public ulong Eon;
        public ulong TxIndex;
        public Bytes32 IdentityPrefix;
        public Address Sender;
        public byte[] EncryptedTransaction;
        public UInt256 GasLimit;
    }
}
