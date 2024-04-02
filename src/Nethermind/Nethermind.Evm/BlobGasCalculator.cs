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
        CalculateBlobGas(transaction.BlobVersionedHashes?.Length ?? 0);

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

    public static bool TryCalculateBlobBaseFee(BlockHeader header, Transaction transaction, out UInt256 blobBaseFee)
    {
        if (!TryCalculateFeePerBlobGas(header.ExcessBlobGas.Value, out UInt256 feePerBlobGas))
        {
            blobBaseFee = UInt256.MaxValue;
            return false;
        }
        return !UInt256.MultiplyOverflow(CalculateBlobGas(transaction), feePerBlobGas, out blobBaseFee);
    }

    public static bool TryCalculateFeePerBlobGas(BlockHeader header, out UInt256 feePerBlobGas)
    {
        feePerBlobGas = UInt256.MaxValue;
        return header.ExcessBlobGas is not null
            && TryCalculateFeePerBlobGas(header.ExcessBlobGas.Value, out feePerBlobGas);
    }

    public static bool TryCalculateFeePerBlobGas(ulong excessBlobGas, out UInt256 feePerBlobGas)
    {
        static bool FakeExponentialOverflow(UInt256 factor, UInt256 num, UInt256 denominator, out UInt256 feePerBlobGas)
        {
            UInt256 output = UInt256.Zero;

            if (UInt256.MultiplyOverflow(factor, denominator, out UInt256 numAccum))
            {
                feePerBlobGas = UInt256.MaxValue;
                return true;
            }

            for (UInt256 i = 1; numAccum > 0; i++)
            {
                if (UInt256.AddOverflow(output, numAccum, out output))
                {
                    feePerBlobGas = UInt256.MaxValue;
                    return true;
                }

                if (UInt256.MultiplyOverflow(numAccum, num, out UInt256 updatedNumAccum))
                {
                    feePerBlobGas = UInt256.MaxValue;
                    return true;
                }

                if (UInt256.MultiplyOverflow(i, denominator, out UInt256 multipliedDeniminator))
                {
                    feePerBlobGas = UInt256.MaxValue;
                    return true;
                }

                numAccum = updatedNumAccum / multipliedDeniminator;
            }

            feePerBlobGas = output / denominator;
            return false;
        }

        return !FakeExponentialOverflow(Eip4844Constants.MinBlobGasPrice, excessBlobGas, Eip4844Constants.BlobGasPriceUpdateFraction, out feePerBlobGas);
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
        excessBlobGas += parentBlockHeader.BlobGasUsed ?? 0;
        return excessBlobGas < Eip4844Constants.TargetBlobGasPerBlock
            ? 0
            : (excessBlobGas - Eip4844Constants.TargetBlobGasPerBlock);
    }
}
