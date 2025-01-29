// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm;
using Nethermind.Facade.Eth;
using Nethermind.Int256;

namespace Nethermind.Optimism.CL.Derivation;

public class L1BlockInfo
{
    public required uint MethodId;
    public required uint BaseFeeScalar;
    public required uint BlobBaseFeeScalar;
    public required ulong SequenceNumber;
    public required ulong Timestamp;
    public required ulong Number;
    public required UInt256 BaseFee;
    public required UInt256 BlobBaseFee;
    public required Hash256 BlockHash;
    public required Address BatcherAddress;
}

public class L1BlockInfoBuilder
{
    private const int SystemTxDataLengthEcotone = 164;
    private static readonly byte[] ExpectedAddressPadding = new byte[12];

    public static L1BlockInfo FromL2DepositTxDataAndExtraData(ReadOnlySpan<byte> depositTxData, ReadOnlySpan<byte> extraData)
    {
        if (depositTxData.Length != SystemTxDataLengthEcotone)
        {
            throw new ArgumentException("System tx data length is incorrect");
        }

        // TODO check MethodId
        uint methodId = BinaryPrimitives.ReadUInt32BigEndian(depositTxData.TakeAndMove(4));
        uint baseFeeScalar = BinaryPrimitives.ReadUInt32BigEndian(depositTxData.TakeAndMove(4));
        uint blobBaseFeeScalar = BinaryPrimitives.ReadUInt32BigEndian(depositTxData.TakeAndMove(4));
        ulong sequenceNumber = BinaryPrimitives.ReadUInt64BigEndian(depositTxData.TakeAndMove(8));
        ulong timestamp = BinaryPrimitives.ReadUInt64BigEndian(depositTxData.TakeAndMove(8));
        ulong number = BinaryPrimitives.ReadUInt64BigEndian(depositTxData.TakeAndMove(8));
        UInt256 baseFee = new(depositTxData.TakeAndMove(32), true);
        UInt256 blobBaseFee = new(depositTxData.TakeAndMove(32), true);
        Hash256 blockHash = new(depositTxData.TakeAndMove(32));
        ReadOnlySpan<byte> addressPadding = depositTxData.TakeAndMove(12);
        if (!addressPadding.SequenceEqual(ExpectedAddressPadding))
        {
            throw new ArgumentException("Address padding mismatch");
        }

        Address batcherAddress = new(depositTxData.TakeAndMove(20));
        return new()
        {
            MethodId = methodId,
            BaseFeeScalar = baseFeeScalar,
            BlobBaseFeeScalar = blobBaseFeeScalar,
            SequenceNumber = sequenceNumber,
            Timestamp = timestamp,
            Number = number,
            BaseFee = baseFee,
            BlobBaseFee = blobBaseFee,
            BlockHash = blockHash,
            BatcherAddress = batcherAddress
        };
    }

    public static L1BlockInfo FromL1BlockAndSystemConfig(BlockForRpc block, SystemConfig config)
    {
        BlobGasCalculator.TryCalculateFeePerBlobGas(block.ExcessBlobGas!.Value, out UInt256 feePerBlobGas);
        return new()
        {
            MethodId = 0,
            BaseFeeScalar = config.BaseFeeScalar,
            BlobBaseFeeScalar = config.BlobBaseFeeScalar,
            SequenceNumber = 0,
            Timestamp = block.Timestamp.ToUInt64(null),
            Number = (ulong)block.Number!.Value,
            BaseFee = block.BaseFeePerGas!.Value,
            BlobBaseFee = feePerBlobGas,
            BlockHash = block.Hash,
            BatcherAddress = config.BatcherAddress,
        };
    }
}
