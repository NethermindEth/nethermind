// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.Optimism.CL.Derivation;
using Nethermind.Specs.Forks;

namespace Nethermind.Optimism.CL;

public class L1BlockInfo
{
    public required uint BaseFeeScalar { get; init; }
    public required uint BlobBaseFeeScalar { get; init; }
    public required ulong SequenceNumber { get; init; }
    public required ulong Timestamp { get; init; }
    public required ulong Number { get; init; }
    public required UInt256 BaseFee { get; init; }
    public required UInt256 BlobBaseFee { get; init; }
    public required Hash256 BlockHash { get; init; }
    public required Address BatcherAddress { get; init; }

    public override string ToString()
    {
        return
            $"BaseFeeScalar: {BaseFeeScalar}, BlobBaseFeeScalar: {BlobBaseFeeScalar}, SequenceNumber: {SequenceNumber}, Timestamp: {Timestamp}, " +
            $"Number: {Number}, BaseFee: {BaseFee}, BlobBaseFee: {BlobBaseFee}, BlockHash: {BlockHash}, BatcherAddress: {BatcherAddress}";
    }

    public static readonly L1BlockInfo Empty = new()
    {
        BaseFee = 0,
        BlobBaseFee = 0,
        BaseFeeScalar = 0,
        BatcherAddress = Address.Zero,
        BlobBaseFeeScalar = 0,
        BlockHash = Hash256.Zero,
        Number = 0,
        SequenceNumber = 0,
        Timestamp = 0,
    };
}

public class L1BlockInfoBuilder
{
    public const UInt32 L1InfoTransactionMethodId = 1141530144;

    private const int SystemTxDataLengthEcotone = 164;

    public static L1BlockInfo FromL2DepositTxDataAndExtraData(ReadOnlySpan<byte> depositTxData, ReadOnlySpan<byte> extraData)
    {
        if (depositTxData.Length != SystemTxDataLengthEcotone)
        {
            throw new ArgumentException("System tx data length is incorrect");
        }

        uint methodId = BinaryPrimitives.ReadUInt32BigEndian(depositTxData.TakeAndMove(4));
        if (methodId != L1InfoTransactionMethodId)
        {
            throw new ArgumentException($"MethodId is incorrect. {methodId}");
        }
        uint baseFeeScalar = BinaryPrimitives.ReadUInt32BigEndian(depositTxData.TakeAndMove(4));
        uint blobBaseFeeScalar = BinaryPrimitives.ReadUInt32BigEndian(depositTxData.TakeAndMove(4));
        ulong sequenceNumber = BinaryPrimitives.ReadUInt64BigEndian(depositTxData.TakeAndMove(8));
        ulong timestamp = BinaryPrimitives.ReadUInt64BigEndian(depositTxData.TakeAndMove(8));
        ulong number = BinaryPrimitives.ReadUInt64BigEndian(depositTxData.TakeAndMove(8));
        UInt256 baseFee = new(depositTxData.TakeAndMove(32), true);
        UInt256 blobBaseFee = new(depositTxData.TakeAndMove(32), true);
        Hash256 blockHash = new(depositTxData.TakeAndMove(32));
        ReadOnlySpan<byte> addressPadding = depositTxData.TakeAndMove(12);
        if (!addressPadding.IsZero())
        {
            throw new ArgumentException("Address padding mismatch");
        }

        Address batcherAddress = new(depositTxData.TakeAndMove(20));
        return new()
        {
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

    public static L1BlockInfo FromL1BlockAndSystemConfig(L1Block block, SystemConfig config, ulong sequenceNumber)
    {
        // TODO: fetch BlobBaseFeeUpdateFraction
        BlobGasCalculator.TryCalculateFeePerBlobGas(block.ExcessBlobGas!.Value, Prague.Instance.BlobBaseFeeUpdateFraction, out UInt256 feePerBlobGas);
        return new()
        {
            BaseFeeScalar = config.BaseFeeScalar,
            BlobBaseFeeScalar = config.BlobBaseFeeScalar,
            SequenceNumber = sequenceNumber,
            Timestamp = block.Timestamp.ToUInt64(null),
            Number = block.Number,
            BaseFee = block.BaseFeePerGas!.Value,
            BlobBaseFee = feePerBlobGas,
            BlockHash = block.Hash,
            BatcherAddress = config.BatcherAddress,
        };
    }
}
