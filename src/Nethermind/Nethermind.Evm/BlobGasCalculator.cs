// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Evm;

public static class BlobGasCalculator
{
    /// <summary>
    /// Tries to compute the total blob fee: <c>blobCount × GAS_PER_BLOB × maxFeePerBlobGas</c>.
    /// </summary>
    /// <returns><see langword="false"/> if the multiplication overflows; otherwise <see langword="true"/>.</returns>
    public static bool TryCalculateBlobMaxFee(int blobCount, UInt256 maxFeePerBlobGas, out UInt256 blobFee) =>
        TryCalculateBlobMaxFee((ulong)blobCount, maxFeePerBlobGas, out blobFee);

    public static bool TryCalculateBlobMaxFee(ulong blobCount, UInt256 maxFeePerBlobGas, out UInt256 blobFee) =>
        !UInt256.MultiplyOverflow((UInt256)blobCount * (UInt256)Eip4844Constants.GasPerBlob, maxFeePerBlobGas, out blobFee);

    public static bool TrySubtractBlobFee(IReleaseSpec spec, Transaction tx, ref UInt256 available)
    {
        if (!spec.IsEip4844Enabled || tx.BlobVersionedHashes?.Length is not > 0)
            return true;

        if (!TryCalculateBlobMaxFee((ulong)tx.BlobVersionedHashes.Length, tx.MaxFeePerBlobGas ?? UInt256.Zero, out UInt256 blobFee)
            || blobFee > available)
            return false;

        available -= blobFee;
        return true;
    }

    public static ulong CalculateBlobGas(int blobCount) =>
        CalculateBlobGas((ulong)blobCount);

    public static ulong CalculateBlobGas(ulong blobCount) =>
        blobCount * Eip4844Constants.GasPerBlob;

    public static ulong CalculateBlobGas(Transaction transaction) =>
        CalculateBlobGas(transaction.GetBlobCount());

    public static ulong CalculateBlobGas(Transaction[] transactions)
    {
        ulong blobCount = 0UL;
        foreach (Transaction tx in transactions)
        {
            if (tx.SupportsBlobs)
            {
                blobCount += (ulong)tx.GetBlobCount();
            }
        }

        return CalculateBlobGas(blobCount);
    }

    public static bool TryCalculateBlobBaseFee(BlockHeader header, Transaction transaction, ulong blobGasPriceUpdateFraction, out UInt256 blobBaseFee)
    {
        if (!TryCalculateFeePerBlobGas(header.ExcessBlobGas.Value, blobGasPriceUpdateFraction, out UInt256 feePerBlobGas))
        {
            blobBaseFee = UInt256.MaxValue;
            return false;
        }
        return !UInt256.MultiplyOverflow(CalculateBlobGas(transaction), feePerBlobGas, out blobBaseFee);
    }

    public static bool TryCalculateFeePerBlobGas(BlockHeader header, ulong blobGasPriceUpdateFraction, out UInt256 feePerBlobGas)
    {
        feePerBlobGas = UInt256.MaxValue;
        return header.ExcessBlobGas is not null
            && TryCalculateFeePerBlobGas(header.ExcessBlobGas.Value, blobGasPriceUpdateFraction, out feePerBlobGas);
    }

    public static bool TryCalculateFeePerBlobGas(ulong excessBlobGas, ulong blobGasPriceUpdateFraction, out UInt256 feePerBlobGas)
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

                accumulator = multipliedDenominator.IsZero ? default : updatedAccumulator / multipliedDenominator;
            }

            feePerBlobGas = denominator.IsZero ? default : output / denominator;
            return false;
        }

        UInt256 denominator = blobGasPriceUpdateFraction;
        return !FakeExponentialOverflow(Eip4844Constants.MinBlobGasPrice, excessBlobGas, denominator, out feePerBlobGas);
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
        ulong targetBlobGasPerBlock = releaseSpec.GasCosts.TargetBlobGasPerBlock;

        if (parentBlobGas < targetBlobGasPerBlock)
        {
            return 0;
        }

        if (releaseSpec.IsEip7918Enabled)
        {
            TryCalculateFeePerBlobGas(parentBlockHeader, releaseSpec.BlobBaseFeeUpdateFraction, out UInt256 feePerBlobGas);
            UInt256 floorCost = Eip7918Constants.BlobBaseCost * parentBlockHeader.BaseFeePerGas;
            UInt256 targetCost = Eip4844Constants.GasPerBlob * feePerBlobGas;

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
