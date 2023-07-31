// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Evm;

public static class BlobGasCalculator
{
    public static ulong CalculateBlobGas(int blobCount) =>
        (ulong)blobCount * Eip4844Constants.BlobGasPerBlob;

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

    public static bool TryCalculateBlobGasPrice(BlockHeader header, Transaction transaction, out UInt256 blobGasPrice)
    {
        if (!TryCalculateBlobGasPricePerUnit(header.ExcessBlobGas.Value, out UInt256 blobGasPricePerUnit))
        {
            blobGasPrice = UInt256.MaxValue;
            return false;
        }
        return !UInt256.MultiplyOverflow(CalculateBlobGas(transaction), blobGasPricePerUnit, out blobGasPrice);
    }

    public static bool TryCalculateBlobGasPricePerUnit(BlockHeader header, out UInt256 blobGasPricePerUnit)
    {
        blobGasPricePerUnit = UInt256.MaxValue;
        return header.ExcessBlobGas is not null
            && TryCalculateBlobGasPricePerUnit(header.ExcessBlobGas.Value, out blobGasPricePerUnit);
    }

    public static bool TryCalculateBlobGasPricePerUnit(ulong excessBlobGas, out UInt256 blobGasPricePerUnit)
    {
        static bool FakeExponentialOverflow(UInt256 factor, UInt256 num, UInt256 denominator, out UInt256 blobGasPricePerUnit)
        {
            UInt256 output = UInt256.Zero;

            if (UInt256.MultiplyOverflow(factor, denominator, out UInt256 numAccum))
            {
                blobGasPricePerUnit = UInt256.MaxValue;
                return true;
            }

            for (UInt256 i = 1; numAccum > 0; i++)
            {
                if (UInt256.AddOverflow(output, numAccum, out output))
                {
                    blobGasPricePerUnit = UInt256.MaxValue;
                    return true;
                }

                if (UInt256.MultiplyOverflow(numAccum, num, out UInt256 updatedNumAccum))
                {
                    blobGasPricePerUnit = UInt256.MaxValue;
                    return true;
                }

                if (UInt256.MultiplyOverflow(i, denominator, out UInt256 multipliedDeniminator))
                {
                    blobGasPricePerUnit = UInt256.MaxValue;
                    return true;
                }

                numAccum = updatedNumAccum / multipliedDeniminator;
            }

            blobGasPricePerUnit = output / denominator;
            return false;
        }

        return !FakeExponentialOverflow(Eip4844Constants.MinBlobGasPrice, excessBlobGas, Eip4844Constants.BlobGasUpdateFraction, out blobGasPricePerUnit);
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
