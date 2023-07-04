// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;

namespace Nethermind.Core.Extensions
{
    public static class SpecProviderExtensions
    {
        /// <summary>
        /// this method is here only for getting spec related to receipts.
        /// Reason of adding is that at sometime we dont know the Timestamp.
        /// </summary>
        /// <param name="specProvider"></param>
        /// <param name="blockNumber"></param>
        /// <returns>IReceiptSpec</returns>
        public static IReceiptSpec GetReceiptSpec(this ISpecProvider specProvider, long blockNumber)
        {
            return specProvider.GetSpec(blockNumber, null);
        }

        /// <summary>
        /// this method is here only for getting spec for 1559.
        /// Reason of adding is that at sometime we dont know the Timestamp.
        /// </summary>
        /// <param name="specProvider"></param>
        /// <param name="blockNumber"></param>
        /// <returns>IEip1559Spec</returns>
        public static IEip1559Spec GetSpecFor1559(this ISpecProvider specProvider, long blockNumber)
        {
            return specProvider.GetSpec(blockNumber, null);
        }
    }
}
