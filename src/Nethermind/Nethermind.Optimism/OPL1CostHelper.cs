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

    private static readonly UInt256 BasicDivisor = 1_000_000;

    // Ecotone
    private readonly StorageCell _blobBaseFeeSlot = new(l1BlockAddr, new UInt256(7));
    private readonly StorageCell _baseFeeScalarSlot = new(l1BlockAddr, new UInt256(3));

    private static readonly UInt256 PrecisionMultiplier = 16;
    private static readonly UInt256 PrecisionDivisor = PrecisionMultiplier * BasicDivisor;


    // Fjord
    private static readonly UInt256 L1CostInterceptNeg = 42_585_600;
    private static readonly UInt256 L1CostFastlzCoef = 836_500;

    private static readonly UInt256 MinTransactionSizeScaled = 100 * 1_000_000;
    private static readonly UInt256 FjordDivisor = 1_000_000_000_000;

    [SkipLocalsInit]
    public UInt256 ComputeL1Cost(Transaction tx, BlockHeader header, IWorldState worldState)
    {
        if (tx.IsDeposit())
            return UInt256.Zero;

        UInt256 l1BaseFee = new(worldState.Get(_l1BaseFeeSlot), true);

        if (_opSpecHelper.IsFjord(header))
        {
            UInt256 blobBaseFee = new(worldState.Get(_blobBaseFeeSlot), true);

            ReadOnlySpan<byte> scalarData = worldState.Get(_baseFeeScalarSlot);

            const int baseFeeFieldsStart = 16;
            const int fieldSize = sizeof(uint);

            int l1BaseFeeScalarStart = scalarData.Length > baseFeeFieldsStart ? scalarData.Length - baseFeeFieldsStart : 0;
            int l1BaseFeeScalarEnd = l1BaseFeeScalarStart + (scalarData.Length >= baseFeeFieldsStart ? fieldSize : fieldSize - baseFeeFieldsStart + scalarData.Length);
            UInt256 l1BaseFeeScalar = new(scalarData[l1BaseFeeScalarStart..l1BaseFeeScalarEnd], true);
            UInt256 l1BlobBaseFeeScalar = new(scalarData[l1BaseFeeScalarEnd..(l1BaseFeeScalarEnd + fieldSize)], true);

            uint fastLzSize = ComputeFlzCompressLen(tx);

            return ComputeL1CostFjord(fastLzSize, l1BaseFee, blobBaseFee, l1BaseFeeScalar, l1BlobBaseFeeScalar);
        }

        UInt256 dataGas = ComputeDataGas(tx, _opSpecHelper.IsRegolith(header));

        if (dataGas.IsZero)
            return UInt256.Zero;

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

    // Fjord L1 formula:
    // l1FeeScaled = baseFeeScalar * l1BaseFee * 16 + blobFeeScalar * l1BlobBaseFee
    // estimatedSize = max(minTransactionSize, intercept + fastlzCoef * fastlzSize)
    // l1Cost = estimatedSize * l1FeeScaled / 1e12
    public static UInt256 ComputeL1CostFjord(UInt256 fastLzSize, UInt256 l1BaseFee, UInt256 blobBaseFee, UInt256 l1BaseFeeScalar, UInt256 l1BlobBaseFeeScalar)
    {
        UInt256 l1FeeScaled = l1BaseFeeScalar * l1BaseFee * PrecisionMultiplier + l1BlobBaseFeeScalar * blobBaseFee;
        UInt256 fastLzCost = L1CostFastlzCoef * fastLzSize;

        if (fastLzCost < L1CostInterceptNeg)
        {
            fastLzCost = 0;
        }
        else
        {
            fastLzCost -= L1CostInterceptNeg;
        }

        var estimatedSize = UInt256.Max(MinTransactionSizeScaled, fastLzCost);
        return estimatedSize * l1FeeScaled / FjordDivisor;
    }

    // Ecotone formula: (dataGas) * (16 * l1BaseFee * l1BaseFeeScalar + l1BlobBaseFee*l1BlobBaseFeeScalar) / 16e6
    public static UInt256 ComputeL1CostEcotone(UInt256 dataGas, UInt256 l1BaseFee, UInt256 blobBaseFee, UInt256 l1BaseFeeScalar, UInt256 l1BlobBaseFeeScalar)
    {
        return dataGas * (PrecisionMultiplier * l1BaseFee * l1BaseFeeScalar + blobBaseFee * l1BlobBaseFeeScalar) / PrecisionDivisor;
    }

    // Pre-Ecotone formula: (dataGas + overhead) * l1BaseFee * scalar / 1e6
    public static UInt256 ComputeL1CostPreEcotone(UInt256 dataGasWithOverhead, UInt256 l1BaseFee, UInt256 feeScalar)
    {
        return dataGasWithOverhead * l1BaseFee * feeScalar / BasicDivisor;
    }

    [SkipLocalsInit]
    public static uint ComputeFlzCompressLen(Transaction tx)
    {
        byte[] encoded = Rlp.Encode(tx, RlpBehaviors.SkipTypedWrapping).Bytes;

        static uint FlzCompressLen(byte[] data)
        {
            uint n = 0;
            uint[] ht = new uint[8192];
            uint u24(uint i) => data[i] | ((uint)data[i + 1] << 8) | ((uint)data[i + 2] << 16);
            uint cmp(uint p, uint q, uint e)
            {
                uint l = 0;
                for (e -= q; l < e; l++)
                {
                    if (data[p + (int)l] != data[q + (int)l])
                    {
                        e = 0;
                    }
                }
                return l;
            }
            void literals(uint r)
            {
                n += 0x21 * (r / 0x20);
                r %= 0x20;
                if (r != 0)
                {
                    n += r + 1;
                }
            }
            void match(uint l)
            {
                l--;
                n += 3 * (l / 262);
                if (l % 262 >= 6)
                {
                    n += 3;
                }
                else
                {
                    n += 2;
                }
            }
            uint hash(uint v) => ((2654435769 * v) >> 19) & 0x1fff;
            uint setNextHash(uint ip)
            {
                ht[hash(u24(ip))] = ip;
                return ip + 1;
            }
            uint a = 0;
            uint ipLimit = (uint)data.Length - 13;
            if (data.Length < 13)
            {
                ipLimit = 0;
            }
            for (uint ip = a + 2; ip < ipLimit;)
            {
                uint d;
                uint r;
                for (; ; )
                {
                    uint s = u24(ip);
                    uint h = hash(s);
                    r = ht[h];
                    ht[h] = ip;
                    d = ip - r;
                    if (ip >= ipLimit)
                    {
                        break;
                    }
                    ip++;
                    if (d <= 0x1fff && s == u24(r))
                    {
                        break;
                    }
                }
                if (ip >= ipLimit)
                {
                    break;
                }
                ip--;
                if (ip > a)
                {
                    literals(ip - a);
                }
                uint l = cmp(r + 3, ip + 3, ipLimit + 9);
                match(l);
                ip = setNextHash(setNextHash(ip + l));
                a = ip;
            }
            literals((uint)data.Length - a);
            return n;
        }

        return FlzCompressLen(encoded);
    }
}
