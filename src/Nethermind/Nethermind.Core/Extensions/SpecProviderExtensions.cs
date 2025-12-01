// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Core.Specs;

namespace Nethermind.Core.Extensions
{
    public static class SpecProviderExtensions
    {
        /// <summary>
        /// This method only retrieves the spec related to receipts.
        /// Reason for adding it is that sometimes we don't know the timestamp.
        /// </summary>
        /// <param name="specProvider"></param>
        /// <param name="blockNumber"></param>
        /// <returns>IReceiptSpec</returns>
        public static IReceiptSpec GetReceiptSpec(this ISpecProvider specProvider, long blockNumber)
        {
            return specProvider.GetSpec(blockNumber, null);
        }

        /// <summary>
        /// This method only retrieves the spec for 1559.
        /// Reason for adding it is that sometimes we don't know the timestamp.
        /// </summary>
        /// <param name="specProvider"></param>
        /// <param name="blockNumber"></param>
        /// <returns>IEip1559Spec</returns>
        public static IEip1559Spec GetSpecFor1559(this ISpecProvider specProvider, long blockNumber)
        {
            return specProvider.GetSpec(blockNumber, null);
        }

        public static ulong GetFinalMaxBlobGasPerBlock(this ISpecProvider specProvider)
        {
            return Eip4844Constants.GasPerBlob * specProvider.GetFinalSpec().MaxBlobCount;
        }
    }
}
