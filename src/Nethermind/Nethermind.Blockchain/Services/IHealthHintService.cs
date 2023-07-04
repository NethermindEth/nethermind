// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Blockchain.Services
{
    public static class HealthHintConstants
    {
        public const int ProcessingSafetyMultiplier = 4;

        public static ulong? InfinityHint = null;

        public const int EthashStandardProcessingPeriod = 15;

        public const int EthashProcessingSafetyMultiplier = 12;

        public const int ProducingSafetyMultiplier = 2;
    }

    public interface IHealthHintService
    {
        /// <summary>
        /// Get processing time assumption based on the network.
        /// </summary>
        /// <returns><value>null</value> if we cannot assume processing interval, otherwise returns the number of seconds for maximum time without processed block</returns>
        ulong? MaxSecondsIntervalForProcessingBlocksHint();

        /// <summary>
        /// Get producing time assumption based on the network.
        /// </summary>
        /// <returns><value>null</value> if we cannot assume producing interval, otherwise returns the number of seconds for maximum time without produced block</returns>
        ulong? MaxSecondsIntervalForProducingBlocksHint();
    }
}
