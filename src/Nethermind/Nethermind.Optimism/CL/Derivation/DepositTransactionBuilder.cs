// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Optimism.CL.Derivation;

public class DepositTransactionBuilder(ISpecProvider specProvider, CLChainSpecEngineParameters engineParameters)
{
    private const int SystemTxDataLengthEcotone = 164;

    public Transaction BuildSystemTransaction(L1BlockInfo blockInfo)
    {
        byte[] data = new byte[SystemTxDataLengthEcotone];
        BinaryPrimitives.WriteUInt32BigEndian(data[..4], blockInfo.MethodId);
        BinaryPrimitives.WriteUInt32BigEndian(data[4..8], blockInfo.BaseFeeScalar);
        BinaryPrimitives.WriteUInt32BigEndian(data[8..12], blockInfo.BlobBaseFeeScalar);
        BinaryPrimitives.WriteUInt64BigEndian(data[12..20], blockInfo.SequenceNumber);
        BinaryPrimitives.WriteUInt64BigEndian(data[20..28], blockInfo.Timestamp);
        BinaryPrimitives.WriteUInt64BigEndian(data[28..36], blockInfo.Number);
        blockInfo.BaseFee.ToBigEndian().CopyTo(data[36..68].AsSpan());
        blockInfo.BlobBaseFee.ToBigEndian().CopyTo(data[68..100].AsSpan());
        blockInfo.BlockHash.Bytes.CopyTo(data[100..132].AsSpan());
        blockInfo.BatcherAddress.Bytes.CopyTo(data[144..164].AsSpan());

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
            ChainId = specProvider.ChainId,
            SenderAddress = engineParameters.SystemTransactionSender,
            To = engineParameters.SystemTransactionTo,
            GasLimit = 1000000,
            IsOPSystemTransaction = true,
            Value = UInt256.Zero,
            SourceHash = sourceHash
        };
    }

    public Transaction BuildUserDepositTransaction()
    {
        throw new NotImplementedException();
    }

    public Transaction UpgradeTransaction()
    {
        throw new NotImplementedException();
    }

    public Transaction ForceIncludeTransaction()
    {
        throw new NotImplementedException();
    }
}
