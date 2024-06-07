// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.State;

namespace Nethermind.Optimism;

public class OPL1CostHelper(IOptimismSpecHelper opSpecHelper, Address l1BlockAddr) : IL1CostHelper
{
    private readonly IOptimismSpecHelper _opSpecHelper = opSpecHelper;

    private readonly StorageCell _l1BaseFeeSlot = new(l1BlockAddr, new UInt256(1));
    private readonly StorageCell _overheadSlot = new(l1BlockAddr, new UInt256(5));
    private readonly StorageCell _scalarSlot = new(l1BlockAddr, new UInt256(6));

    private static readonly UInt256 basicDevider = 1_000_000;

    // Ecotone
    private readonly StorageCell _blobBaseFeeSlot = new(l1BlockAddr, new UInt256(7));
    private readonly StorageCell _baseFeeScalarSlot = new(l1BlockAddr, new UInt256(3));

    private static readonly UInt256 precisionMultiplier = 16;
    private static readonly UInt256 precisionDevider = precisionMultiplier * basicDevider;

    [SkipLocalsInit]
    public UInt256 ComputeL1Cost(Transaction tx, BlockHeader header, IWorldState worldState)
    {
        if (tx.IsDeposit())
            return UInt256.Zero;

        UInt256 dataGas = ComputeDataGas(tx, _opSpecHelper.IsRegolith(header));
        if (dataGas.IsZero)
            return UInt256.Zero;

        UInt256 l1BaseFee = new(worldState.Get(_l1BaseFeeSlot), true);

        if (_opSpecHelper.IsEcotone(header))
        {
            UInt256 blobBaseFee = new(worldState.Get(_blobBaseFeeSlot), true);

            ReadOnlySpan<byte> scalarData = worldState.Get(_baseFeeScalarSlot);

            const int baseFeeFieldsStart = 16;
            const int fieldSize = sizeof(uint);

            int l1BaseFeeScalarStart = scalarData.Length > baseFeeFieldsStart ? scalarData.Length - baseFeeFieldsStart : 0;
            int l1BaseFeeScalarEnd = l1BaseFeeScalarStart + (scalarData.Length >= baseFeeFieldsStart ? fieldSize : fieldSize - baseFeeFieldsStart + scalarData.Length);
            UInt256 l1BaseFeeScalar = new(scalarData[l1BaseFeeScalarStart..l1BaseFeeScalarEnd], true);
            UInt256 l1BlobBaseFeeScalar = new(scalarData[l1BaseFeeScalarEnd..(l1BaseFeeScalarEnd + fieldSize)], true);

            return ComputeL1CostEcotone(dataGas, l1BaseFee, blobBaseFee, l1BaseFeeScalar, l1BlobBaseFeeScalar);
        }
        else
        {
            UInt256 overhead = new(worldState.Get(_overheadSlot), true);
            UInt256 feeScalar = new(worldState.Get(_scalarSlot), true);

            return ComputeL1CostPreEcotone(dataGas + overhead, l1BaseFee, feeScalar);
        }
    }

    [SkipLocalsInit]
    public static UInt256 ComputeDataGas(Transaction tx, bool isRegolith)
    {
        byte[] encoded = Rlp.Encode(tx, RlpBehaviors.SkipTypedWrapping).Bytes;

        long zeroCount = encoded.Count(b => b == 0);
        long nonZeroCount = encoded.Length - zeroCount;
        // Add pre-EIP-3529 overhead
        nonZeroCount += isRegolith ? 0 : OptimismConstants.PreRegolithNonZeroCountOverhead;

        return (ulong)(zeroCount * GasCostOf.TxDataZero + nonZeroCount * GasCostOf.TxDataNonZeroEip2028);
    }

    // Ecotone formula: (dataGas) * (16 * l1BaseFee * l1BaseFeeScalar + l1BlobBaseFee*l1BlobBaseFeeScalar) / 16e6
    public static UInt256 ComputeL1CostEcotone(UInt256 dataGas, UInt256 l1BaseFee, UInt256 blobBaseFee, UInt256 l1BaseFeeScalar, UInt256 l1BlobBaseFeeScalar)
    {
        return dataGas * (precisionMultiplier * l1BaseFee * l1BaseFeeScalar + blobBaseFee * l1BlobBaseFeeScalar) / precisionDevider;
    }

    // Pre-Ecotone formula: (dataGas + overhead) * l1BaseFee * scalar / 1e6
    public static UInt256 ComputeL1CostPreEcotone(UInt256 dataGasWithOverhead, UInt256 l1BaseFee, UInt256 feeScalar)
    {
        return dataGasWithOverhead * l1BaseFee * feeScalar / basicDevider;
    }
}
