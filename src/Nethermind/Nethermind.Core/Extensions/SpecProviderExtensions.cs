// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
        public static IReceiptSpec GetReceiptSpec(this ISpecProvider specProvider, ulong blockNumber) => specProvider.GetSpec(blockNumber, null);

        /// <summary>
        /// This method only retrieves the spec for 1559.
        /// Reason for adding it is that sometimes we don't know the timestamp.
        /// </summary>
        /// <param name="specProvider"></param>
        /// <param name="blockNumber"></param>
        /// <returns>IEip1559Spec</returns>
        public static IEip1559Spec GetSpecFor1559(this ISpecProvider specProvider, ulong blockNumber) => specProvider.GetSpec(blockNumber, null);

        /// <summary>The first timestamp from which EIP-8198 is active, or null when it is not scheduled.</summary>
        public static ulong? GetEip8198Timestamp(this ISpecProvider specProvider)
        {
            if (specProvider.GenesisSpec.IsEip8198Enabled) return 0;

            ulong? first = null;
            foreach (ForkActivation activation in specProvider.TransitionActivations)
            {
                if (activation.Timestamp is { } timestamp
                    && timestamp < (first ?? ulong.MaxValue)
                    && specProvider.GetSpec(activation).IsEip8198Enabled)
                {
                    first = timestamp;
                }
            }

            return first;
        }

        /// <summary>The beacon slot grid for <paramref name="secondsPerSlot"/>, the slot length before EIP-8198.</summary>
        public static SlotSchedule GetSlotSchedule(this ISpecProvider specProvider, ulong secondsPerSlot) =>
            new(secondsPerSlot, specProvider.BeaconChainGenesisTimestamp, specProvider.GetEip8198Timestamp());

        public static ulong GetFinalMaxBlobGasPerBlock(this ISpecProvider specProvider) => Eip4844Constants.GasPerBlob * specProvider.GetFinalSpec().MaxBlobCount;
    }
}
