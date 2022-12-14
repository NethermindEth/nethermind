// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Evm
{
    public static class IntrinsicGasCalculator
    {
        public static long Calculate(Transaction transaction, IReleaseSpec releaseSpec/*, UInt256 parentExcessDataGas*/)
        {
            long result = GasCostOf.Transaction;
            result += DataCost(transaction, releaseSpec);
            result += CreateCost(transaction, releaseSpec);
            result += AccessListCost(transaction, releaseSpec);
            return result;
        }

        private static long CreateCost(Transaction transaction, IReleaseSpec releaseSpec)
        {
            long createCost = 0;
            if (transaction.IsContractCreation && releaseSpec.IsEip2Enabled)
            {
                createCost += GasCostOf.TxCreate;
            }

            return createCost;
        }

        private static long DataCost(Transaction transaction, IReleaseSpec releaseSpec)
        {
            long txDataNonZeroGasCost =
                releaseSpec.IsEip2028Enabled ? GasCostOf.TxDataNonZeroEip2028 : GasCostOf.TxDataNonZero;
            long dataCost = 0;
            if (transaction.Data is not null)
            {
                for (int i = 0; i < transaction.Data.Length; i++)
                {
                    dataCost += transaction.Data[i] == 0 ? GasCostOf.TxDataZero : txDataNonZeroGasCost;
                }
            }

            if (transaction.IsContractCreation && releaseSpec.IsEip3860Enabled)
            {
                dataCost += EvmPooledMemory.Div32Ceiling((UInt256)transaction.Data.Length) * GasCostOf.InitCodeWord;
            }

            return dataCost;
        }

        private static long AccessListCost(Transaction transaction, IReleaseSpec releaseSpec)
        {
            AccessList? accessList = transaction.AccessList;
            long accessListCost = 0;
            if (accessList is not null)
            {
                if (releaseSpec.UseTxAccessLists)
                {
                    if (accessList.IsNormalized)
                    {
                        accessListCost += accessList.Data.Count * GasCostOf.AccessAccountListEntry;
                        accessListCost += accessList.Data.Sum(d => d.Value.Count) *
                                          GasCostOf.AccessStorageListEntry;
                    }
                    else
                    {
                        foreach (object o in accessList.OrderQueue!)
                        {
                            if (o is Address)
                            {
                                accessListCost += GasCostOf.AccessAccountListEntry;
                            }
                            else
                            {
                                accessListCost += GasCostOf.AccessStorageListEntry;
                            }
                        }
                    }
                }
                else
                {
                    throw new InvalidDataException(
                        $"Transaction with an access list received within the context of {releaseSpec.Name}. Eip-2930 is not enabled.");
                }
            }

            return accessListCost;
        }

        static UInt256 DataGasPriceUpdateFraction = 2225652;
        static UInt256 MinDataGasPrice = 1L;

        // FakeExponential
        public static UInt256 BlobsGas(Transaction transaction, UInt256 parentExcessDataGas, IReleaseSpec releaseSpec)
        {
            if (!releaseSpec.IsEip4844Enabled || transaction.Type != TxType.Blob || transaction.BlobVersionedHashes?.Any() != true)
            {
                return 0;
            }
            if(parentExcessDataGas == UInt256.MaxValue)
            {
                throw new InvalidOperationException();
            }

            return (ulong)transaction.BlobVersionedHashes!.Length * CostPerBlob(parentExcessDataGas);
        }

        public static UInt256 CostPerBlob(UInt256 parentExcessDataGas)
        {
            UInt256 FakeExponential(UInt256 factor, UInt256 num, UInt256 denom)
            {
                UInt256 output = UInt256.Zero;

                UInt256 numAccum = factor * denom;

                for (UInt256 i = 1; numAccum > 0; i++)
                {
                    output += numAccum;
                    numAccum *= num;
                    numAccum /= i * denom;
                }
                return output / denom;
            }

            var r = FakeExponential(MinDataGasPrice, parentExcessDataGas, DataGasPriceUpdateFraction);
            return r * 1<<17;
        }

        static UInt256 DataGasPerBlob = 1 << 19;
        static UInt256 TargetDataGasPerBlock = 1 << 18;

        public static UInt256 CalcExcessDataGas(UInt256? parentExcessDataGas, int newBlobs)
        {
            UInt256 excessDataGas = parentExcessDataGas.GetValueOrDefault();
            UInt256 consumedGas = DataGasPerBlob * (UInt256)newBlobs;
            excessDataGas += consumedGas;
            return excessDataGas < TargetDataGasPerBlock ? UInt256.Zero : (excessDataGas - TargetDataGasPerBlock);
        }
    }
}
