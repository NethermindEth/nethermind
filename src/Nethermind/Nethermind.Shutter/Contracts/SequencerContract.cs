// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Contracts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Experimental.Abi;
using Nethermind.Int256;

namespace Nethermind.Shutter.Contracts;

public class SequencerContract(Address address) : Contract(address), ISequencerContract
{
    /// <summary>Hash of <c>TransactionSubmitted(uint64,uint64,bytes32,address,bytes,uint256)</c>, the first topic of its logs.</summary>
    public static readonly Hash256 TransactionSubmittedHash = new AbiSignature(nameof(ISequencerContract.TransactionSubmitted))
        .With(AbiType.UInt64, AbiType.UInt64, AbiType.BytesM(32), AbiType.Address, AbiType.Bytes, AbiType.UInt256).Hash;

    /// <summary>Signature of the <c>TransactionSubmitted</c> log data.</summary>
    public static readonly AbiSignature<(ulong Eon, ulong TxIndex, byte[] IdentityPrefix, Address Sender, byte[] EncryptedTransaction, UInt256 GasLimit)> TransactionSubmittedData = new AbiSignature()
        .With(AbiType.UInt64, AbiType.UInt64, AbiType.BytesM(32), AbiType.Address, AbiType.Bytes, AbiType.UInt256);
}
