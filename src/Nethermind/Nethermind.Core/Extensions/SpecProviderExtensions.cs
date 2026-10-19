// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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

        /// <summary>
        /// The beacon slot grid for <paramref name="secondsPerSlot"/>, the slot length before EIP-8198, and the chain's
        /// <see cref="ISpecProvider.Eip8198SlotDurationMs"/> from the fork.
        /// </summary>
        /// <remarks>The EIP-8198 slot duration is only read when the fork is scheduled.</remarks>
        /// <exception cref="InvalidOperationException">EIP-8198 is scheduled but the chain sets no slot duration.</exception>
        public static SlotSchedule GetSlotSchedule(this ISpecProvider specProvider, ulong secondsPerSlot)
        {
            if (specProvider.GetEip8198Timestamp() is not { } eip8198Timestamp)
                return new(secondsPerSlot, specProvider.BeaconChainGenesisTimestamp);

            ulong slotDurationMs = specProvider.Eip8198SlotDurationMs ?? throw new InvalidOperationException(
                $"EIP-8198 is scheduled at timestamp {eip8198Timestamp}, but the chain sets no EIP-8198 slot duration (eip8198SlotDurationMs).");
            return new(secondsPerSlot, specProvider.BeaconChainGenesisTimestamp, eip8198Timestamp, slotDurationMs);
        }

        public static ulong GetFinalMaxBlobGasPerBlock(this ISpecProvider specProvider) => Eip4844Constants.GasPerBlob * specProvider.GetFinalSpec().MaxBlobCount;
    }
}
