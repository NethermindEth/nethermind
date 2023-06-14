// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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

    public static UInt256 CalculateDataGasPrice(BlockHeader header, Transaction transaction) =>
        CalculateDataGas(transaction) * CalculateDataGasPricePerUnit(header);

    public static UInt256 CalculateDataGasPricePerUnit(BlockHeader header) =>
        header.ExcessDataGas is null
            ? throw new ArgumentException(nameof(BlockHeader.ExcessDataGas))
            : CalculateDataGasPricePerUnit(header.ExcessDataGas.Value);

    public static UInt256 CalculateDataGasPricePerUnit(ulong excessDataGas)
    {
        static UInt256 FakeExponential(UInt256 factor, UInt256 num, UInt256 denominator)
        {
            UInt256 output = UInt256.Zero;

            UInt256 numAccum = factor * denominator;

            for (UInt256 i = 1; numAccum > 0; i++)
            {
                output += numAccum;
                numAccum *= num;
                numAccum /= i * denominator;
            }

            return output / denominator;
        }

        UInt256 scaleDueToParentExcessDataGas =
            FakeExponential(Eip4844Constants.MinDataGasPrice, excessDataGas, Eip4844Constants.DataGasUpdateFraction);
        return scaleDueToParentExcessDataGas;
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

    public static ulong? CalculateExcessDataGas(ulong? parentExcessDataGas, ulong? parentDataGasUsed)
    {
        ulong excessDataGas = parentExcessDataGas ?? 0;
        excessDataGas += parentDataGasUsed ?? 0;
        return excessDataGas < Eip4844Constants.TargetDataGasPerBlock
            ? 0
            : (excessDataGas - Eip4844Constants.TargetDataGasPerBlock);
    }
}
