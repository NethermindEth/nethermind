// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Optimism.CL.Derivation;

public class DepositTransactionBuilder(ulong chainId, CLChainSpecEngineParameters engineParameters)
{
    private const int SystemTxDataLengthEcotone = 164;

    public Transaction BuildSystemTransaction(L1BlockInfo blockInfo)
    {
        byte[] data = new byte[SystemTxDataLengthEcotone];
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(), blockInfo.MethodId);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4), blockInfo.BaseFeeScalar);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), blockInfo.BlobBaseFeeScalar);
        BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(12), blockInfo.SequenceNumber);
        BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(20), blockInfo.Timestamp);
        BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(28), blockInfo.Number);
        blockInfo.BaseFee.ToBigEndian().CopyTo(data, 36);
        blockInfo.BlobBaseFee.ToBigEndian().CopyTo(data, 68);
        blockInfo.BlockHash.Bytes.CopyTo(data.AsSpan(100));
        blockInfo.BatcherAddress.Bytes.CopyTo(data, 144);

        Span<byte> source = stackalloc byte[64];
        blockInfo.BlockHash.Bytes.CopyTo(source);
        BinaryPrimitives.WriteUInt64BigEndian(source[56..], blockInfo.SequenceNumber);
        Hash256 depositInfoHash = Keccak.Compute(source);

        source.Fill(0);
        BinaryPrimitives.WriteUInt64BigEndian(source[24..32], 1);
        depositInfoHash.Bytes.CopyTo(source[32..]);
        Hash256 sourceHash = Keccak.Compute(source);

        return new()
        {
            Type = TxType.DepositTx,
            Data = data,
            ChainId = chainId,
            SenderAddress = engineParameters.SystemTransactionSender,
            To = engineParameters.SystemTransactionTo,
            GasLimit = 1000000,
            IsOPSystemTransaction = true,
            Value = UInt256.Zero,
            SourceHash = sourceHash
        };
    }

    public Transaction[] BuildUserDepositTransactions()
    {
        return []; // TODO implement
    }

    public Transaction[] BuildUpgradeTransactions()
    {
        return []; // TODO implement
    }

    public Transaction[] BuildForceIncludeTransactions()
    {
        return []; // TODO implement
    }
}
