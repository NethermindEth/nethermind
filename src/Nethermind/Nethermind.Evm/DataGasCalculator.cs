// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Evm;

public static class DataGasCalculator
{
    public static ulong CalculateDataGas(int blobCount) =>
        (ulong)blobCount * Eip4844Constants.DataGasPerBlob;

    public static ulong CalculateDataGas(Transaction transaction) =>
        CalculateDataGas(transaction.BlobVersionedHashes?.Length ?? 0);

    public static ulong CalculateDataGas(Transaction[] transactions)
    {
        int blobCount = 0;
        foreach (Transaction tx in transactions)
        {
            if (tx.SupportsBlobs)
            {
                blobCount += tx.BlobVersionedHashes!.Length;
            }
        }

        return CalculateDataGas(blobCount);
    }

    public static bool TryCalculateDataGasPrice(BlockHeader header, Transaction transaction, out UInt256 dataGasPrice)
    {
        if (!TryCalculateDataGasPricePerUnit(header.ExcessDataGas.Value, out UInt256 dataGasPricePerUnit))
        {
            dataGasPrice = UInt256.MaxValue;
            return false;
        }
        return !UInt256.MultiplyOverflow(CalculateDataGas(transaction), dataGasPricePerUnit, out dataGasPrice);
    }

    public static bool TryCalculateDataGasPricePerUnit(BlockHeader header, out UInt256 dataGasPricePerUnit)
    {
        dataGasPricePerUnit = UInt256.MaxValue;
        return header.ExcessDataGas is not null
            && TryCalculateDataGasPricePerUnit(header.ExcessDataGas.Value, out dataGasPricePerUnit);
    }

    public static bool TryCalculateDataGasPricePerUnit(ulong excessDataGas, out UInt256 dataGasPricePerUnit)
    {
        static bool FakeExponentialOverflow(UInt256 factor, UInt256 num, UInt256 denominator, out UInt256 dataGasPricePerUnit)
        {
            UInt256 output = UInt256.Zero;

            if (UInt256.MultiplyOverflow(factor, denominator, out UInt256 numAccum))
            {
                dataGasPricePerUnit = UInt256.MaxValue;
                return true;
            }

            for (UInt256 i = 1; numAccum > 0; i++)
            {
                if (UInt256.AddOverflow(output, numAccum, out output))
                {
                    dataGasPricePerUnit = UInt256.MaxValue;
                    return true;
                }

                if (UInt256.MultiplyOverflow(numAccum, num, out UInt256 updatedNumAccum))
                {
                    dataGasPricePerUnit = UInt256.MaxValue;
                    return true;
                }

                if (UInt256.MultiplyOverflow(i, denominator, out UInt256 multipliedDeniminator))
                {
                    dataGasPricePerUnit = UInt256.MaxValue;
                    return true;
                }

                numAccum = updatedNumAccum / multipliedDeniminator;
            }

            dataGasPricePerUnit = output / denominator;
            return false;
        }

        return !FakeExponentialOverflow(Eip4844Constants.MinDataGasPrice, excessDataGas, Eip4844Constants.DataGasUpdateFraction, out dataGasPricePerUnit);
    }

    public static ulong? CalculateExcessDataGas(BlockHeader? parentBlockHeader, IReleaseSpec releaseSpec)
    {
        if (!releaseSpec.IsEip4844Enabled)
        {
            return null;
        }

        if (parentBlockHeader is null)
        {
            return 0;
        }

        ulong excessDataGas = parentBlockHeader.ExcessDataGas ?? 0;
        excessDataGas += parentBlockHeader.DataGasUsed ?? 0;
        return excessDataGas < Eip4844Constants.TargetDataGasPerBlock
            ? 0
            : (excessDataGas - Eip4844Constants.TargetDataGasPerBlock);
    }
}
