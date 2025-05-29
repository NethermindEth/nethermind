// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Evm;

public static class BlobGasCalculator
{
    public static ulong CalculateBlobGas(int blobCount) =>
        (ulong)blobCount * Eip4844Constants.GasPerBlob;

    public static ulong CalculateBlobGas(Transaction transaction) =>
        CalculateBlobGas(transaction.GetBlobCount());

    public static ulong CalculateBlobGas(Transaction[] transactions)
    {
        int blobCount = 0;
        foreach (Transaction tx in transactions)
        {
            if (tx.SupportsBlobs)
            {
                blobCount += tx.BlobVersionedHashes!.Length;
            }
        }

        return CalculateBlobGas(blobCount);
    }

    public static bool TryCalculateBlobBaseFee(BlockHeader header, Transaction transaction, UInt256 blobGasPriceUpdateFraction, out UInt256 blobBaseFee)
    {
        if (!TryCalculateFeePerBlobGas(header.ExcessBlobGas.Value, blobGasPriceUpdateFraction, out UInt256 feePerBlobGas))
        {
            blobBaseFee = UInt256.MaxValue;
            return false;
        }
        return !UInt256.MultiplyOverflow(CalculateBlobGas(transaction), feePerBlobGas, out blobBaseFee);
    }

    public static bool TryCalculateFeePerBlobGas(BlockHeader header, UInt256 blobGasPriceUpdateFraction, out UInt256 feePerBlobGas)
    {
        feePerBlobGas = UInt256.MaxValue;
        return header.ExcessBlobGas is not null
            && TryCalculateFeePerBlobGas(header.ExcessBlobGas.Value, blobGasPriceUpdateFraction, out feePerBlobGas);
    }

    public static bool TryCalculateFeePerBlobGas(ulong excessBlobGas, UInt256 blobGasPriceUpdateFraction, out UInt256 feePerBlobGas)
    {
        static bool FakeExponentialOverflow(UInt256 factor, UInt256 num, UInt256 denominator, out UInt256 feePerBlobGas)
        {
            UInt256 accumulator;
            if (factor == UInt256.One)
            {
                // Skip expensive 256bit multiplication if factor is 1
                accumulator = denominator;
            }
            else if (UInt256.MultiplyOverflow(factor, denominator, out accumulator))
            {
                feePerBlobGas = UInt256.MaxValue;
                return true;
            }

            UInt256 output = default;
            for (ulong i = 1; !accumulator.IsZero; i++)
            {
                if (UInt256.AddOverflow(output, accumulator, out output))
                {
                    feePerBlobGas = UInt256.MaxValue;
                    return true;
                }

                if (UInt256.MultiplyOverflow(accumulator, num, out UInt256 updatedAccumulator))
                {
                    feePerBlobGas = UInt256.MaxValue;
                    return true;
                }

                if (UInt256.MultiplyOverflow(i, denominator, out UInt256 multipliedDenominator))
                {
                    feePerBlobGas = UInt256.MaxValue;
                    return true;
                }

                accumulator = updatedAccumulator / multipliedDenominator;
            }

            feePerBlobGas = output / denominator;
            return false;
        }

        return !FakeExponentialOverflow(Eip4844Constants.MinBlobGasPrice, excessBlobGas, blobGasPriceUpdateFraction, out feePerBlobGas);
    }

    public static ulong? CalculateExcessBlobGas(BlockHeader? parentBlockHeader, IReleaseSpec releaseSpec)
    {
        if (!releaseSpec.IsEip4844Enabled)
        {
            return null;
        }

        if (parentBlockHeader is null)
        {
            return 0;
        }

        ulong excessBlobGas = parentBlockHeader.ExcessBlobGas ?? 0;
        ulong blobGasUsed = parentBlockHeader.BlobGasUsed ?? 0;
        ulong parentBlobGas = excessBlobGas + blobGasUsed;
        ulong targetBlobGasPerBlock = releaseSpec.GetTargetBlobGasPerBlock();

        if (parentBlobGas < targetBlobGasPerBlock)
        {
            return 0;
        }

        if (releaseSpec.IsEip7918Enabled)
        {
            TryCalculateFeePerBlobGas(parentBlockHeader, releaseSpec.BlobBaseFeeUpdateFraction, out UInt256 feePerBlobGas);
            UInt256 floorCost = Eip7918Constants.BlobBaseCost * parentBlockHeader.BaseFeePerGas;
            UInt256 targetCost = targetBlobGasPerBlock * feePerBlobGas;

            // if below floor cost then increase excess blob gas
            if (floorCost > targetCost)
            {
                ulong target = releaseSpec.TargetBlobCount;
                ulong max = releaseSpec.MaxBlobCount;
                return excessBlobGas + (blobGasUsed * (max - target) / max);
            }
        }

        return parentBlobGas - targetBlobGasPerBlock;
    }
}
