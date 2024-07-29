// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Extensions;

namespace Nethermind.Blockchain
{
    public interface IChainEstimations
    {
        long? StateSize { get; }
        long? PruningSize { get; }
    }

    public static class ChainSizes
    {
        public class UnknownChain : IChainEstimations
        {
            public long? StateSize => null;
            public long? PruningSize => null;

            public static readonly IChainEstimations Instance = new UnknownChain();
        }

        private class ChainEstimations(
            LinearExtrapolation? stateSizeEstimator = null,
            LinearExtrapolation? prunedStateEstimator = null)
            : IChainEstimations
        {
            public long? StateSize => stateSizeEstimator?.Estimate;
            public long? PruningSize => prunedStateEstimator?.Estimate;
        }

        private class LinearExtrapolation
        {
            private readonly long _atUpdate;
            private readonly long _dailyGrowth;
            private readonly DateTime _updateDate;

            public LinearExtrapolation(long atUpdate, long dailyGrowth, DateTime updateDate)
            {
                _atUpdate = atUpdate;
                _dailyGrowth = dailyGrowth;
                _updateDate = updateDate;
            }

            public LinearExtrapolation(long firstValue, DateTime firstDate, long secondValue, DateTime secondDate)
            {
                _atUpdate = firstValue;
                _dailyGrowth = (long)((secondValue - firstValue) / (secondDate - firstDate).TotalDays);
                _updateDate = firstDate;
            }

            public long Estimate => _atUpdate + (DateTime.UtcNow - _updateDate).Days * _dailyGrowth;
        }

        /// <summary>
        /// Size in bytes, daily growth rate and the date of manual update
        /// </summary>
        public static IChainEstimations CreateChainSizeInfo(ulong chainId)
        {
            return chainId switch
            {
                BlockchainIds.Mainnet => new ChainEstimations(new LinearExtrapolation(156.GB(), 90.MB(), new DateTime(2024, 07, 17)),
                    new LinearExtrapolation(180.GB(), 95.MB(), new DateTime(2024, 07, 17))),
                BlockchainIds.Sepolia => new ChainEstimations(new LinearExtrapolation(38.GB(), 90.MB(), new DateTime(2024, 07, 17)),
                    new LinearExtrapolation(45.GB(), 95.MB(), new DateTime(2024, 07, 17))),

                BlockchainIds.Holesky => new ChainEstimations(new LinearExtrapolation(17.GB(), 30.MB(), new DateTime(2024, 07, 17))),

                BlockchainIds.Gnosis => new ChainEstimations(new LinearExtrapolation(64.GB(), 90.MB(), new DateTime(2024, 07, 17))),
                BlockchainIds.Chiado => new ChainEstimations(new LinearExtrapolation(3.GB(), 5.MB(), new DateTime(2024, 07, 17))),

                BlockchainIds.EnergyWeb => new ChainEstimations(new LinearExtrapolation(26.GB(), 10.MB(), new DateTime(2024, 07, 17))),
                BlockchainIds.Volta => new ChainEstimations(new LinearExtrapolation(34.GB(), 10.MB(), new DateTime(2024, 07, 17))),

                _ => UnknownChain.Instance
            };
        }
    }
}
