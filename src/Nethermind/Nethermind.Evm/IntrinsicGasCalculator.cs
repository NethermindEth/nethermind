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

        /// <summary>
        /// Blobs add additional cost to a transaction. The cost is calculated within separate gas market.
        /// Parent block holds traces of high demand in <see cref="Block.ExcessDataGas"/> field, which value affects
        /// the current block's blob data cost, raises it exponentially.
        /// </summary>
        /// <param name="transaction">Blob transaction</param>
        /// <param name="parentExcessDataGas"><see cref="Block.ExcessDataGas"/> of the parent block</param>
        /// <param name="releaseSpec">The current fork's specification</param>
        /// <returns>Gas cost added by blobs</returns>
        /// <exception cref="InvalidOperationException">Raised in case of inproperly set parentExcessDataGas</exception>
        public static UInt256 CalculateBlobsGasCost(Transaction transaction, UInt256 parentExcessDataGas, IReleaseSpec releaseSpec)
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

        private static UInt256 CostPerBlob(UInt256 parentExcessDataGas)
        {
            UInt256 DataGasPriceUpdateFraction = 2225652;
            UInt256 MinDataGasPrice = 1L;

            UInt256 FakeExponential(UInt256 factor, UInt256 num, UInt256 denominator)
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

            UInt256 scaleDueToParentExcessDataGas = FakeExponential(MinDataGasPrice, parentExcessDataGas, DataGasPriceUpdateFraction);
            return scaleDueToParentExcessDataGas * 1<<17;
        }

        /// <summary>
        /// Calculates the current block's ExcessDataGas, that regulates blob gas market price.
        /// </summary>
        /// <param name="parentExcessDataGas">Parent block's ExcessDataGas</param>
        /// <param name="newBlobsCount">Current block's blob count</param>
        /// <param name="releaseSpec"></param>
        /// <returns>ExcessDataGas</returns>
        public static UInt256 CalculateExcessDataGas(UInt256? parentExcessDataGas, int newBlobsCount,
            IReleaseSpec releaseSpec)
        {
            if (!releaseSpec.IsEip4844Enabled)
            {
                return 0;
            }

            UInt256 DataGasPerBlob = 1 << 17;
            UInt256 TargetDataGasPerBlock = 1 << 18;

            UInt256 excessDataGas = parentExcessDataGas.GetValueOrDefault();
            UInt256 consumedGas = DataGasPerBlob * (UInt256)newBlobsCount;
            excessDataGas += consumedGas;
            return excessDataGas < TargetDataGasPerBlock ? UInt256.Zero : (excessDataGas - TargetDataGasPerBlock);
        }
    }
}
