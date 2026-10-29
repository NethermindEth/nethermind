// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
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
        public static IReceiptSpec GetReceiptSpec(this ISpecProvider specProvider, ulong blockNumber) => specProvider.GetSpec(blockNumber, null);

        /// <summary>
        /// This method only retrieves the spec for 1559.
        /// Reason for adding it is that sometimes we don't know the timestamp.
        /// </summary>
        /// <param name="specProvider"></param>
        /// <param name="blockNumber"></param>
        /// <returns>IEip1559Spec</returns>
        public static IEip1559Spec GetSpecFor1559(this ISpecProvider specProvider, ulong blockNumber) => specProvider.GetSpec(blockNumber, null);

        /// <summary>
        /// The beacon slot grid: <paramref name="secondsPerSlot"/> until a fork changes the slot length, then each change from its
        /// activation.
        /// </summary>
        /// <remarks>
        /// Walks the scheduled timestamp transitions in order and starts a segment wherever
        /// <see cref="DerivedSpecValues.SlotDurationMs"/> of the active spec changes; unscheduled placeholders are skipped. Without
        /// such a fork it is a single segment.
        /// </remarks>
        public static SlotSchedule GetSlotSchedule(this ISpecProvider specProvider, ulong secondsPerSlot)
        {
            ulong? overrideMs = specProvider.Eip8198SlotDurationMs;
            ulong? current = DerivedSpecValues.SlotDurationMs(specProvider.GenesisSpec, overrideMs);
            List<(ulong Timestamp, ulong SlotDurationMs)> changes = current is { } genesisMs ? [(0, genesisMs)] : [];

            foreach (ForkActivation activation in specProvider.TransitionActivations
                         .Where(static a => a.Timestamp <= Specs.SpecProviderExtensions.LastScheduledForkTimestamp)
                         .OrderBy(static a => a.Timestamp))
            {
                ulong? slotDurationMs = DerivedSpecValues.SlotDurationMs(specProvider.GetSpec(activation), overrideMs);
                if (slotDurationMs == current) continue;

                current = slotDurationMs;
                changes.Add((activation.Timestamp!.Value, slotDurationMs ?? Math.Max(1UL, secondsPerSlot) * 1_000));
            }

            return changes.Count == 0
                ? new(secondsPerSlot, specProvider.BeaconChainGenesisTimestamp)
                : new(secondsPerSlot, specProvider.BeaconChainGenesisTimestamp, changes);
        }

        public static ulong GetFinalMaxBlobGasPerBlock(this ISpecProvider specProvider) => Eip4844Constants.GasPerBlob * specProvider.GetFinalSpec().MaxBlobCount;
    }
}
