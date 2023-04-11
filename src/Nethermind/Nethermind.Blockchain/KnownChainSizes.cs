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
    }

    public static class ChainSizes
    {
        public class UnknownChain : IChainEstimations
        {
            public long? StateSize => null;

            public static readonly IChainEstimations Instance = new UnknownChain();
        }

        private class ChainEstimations : IChainEstimations
        {
            private readonly LinearExtrapolation _stateSizeEstimator;

            public ChainEstimations(LinearExtrapolation stateSizeEstimator)
            {
                _stateSizeEstimator = stateSizeEstimator;
            }

            public long? StateSize => _stateSizeEstimator.Estimate;
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

            public long Estimate => _atUpdate + (DateTime.UtcNow - _updateDate).Days * _dailyGrowth;
        }

        /// <summary>
        /// Size in bytes, daily growth rate and the date of manual update
        /// </summary>
        public static IChainEstimations CreateChainSizeInfo(ulong chainId)
        {
            return chainId switch
            {
                BlockchainIds.Goerli => new ChainEstimations(
                    new LinearExtrapolation(8490.MB(), 15.MB(), new DateTime(2021, 12, 7))),
                BlockchainIds.Rinkeby => new ChainEstimations(
                    new LinearExtrapolation(34700.MB(), 20.MB(), new DateTime(2021, 12, 7))),
                BlockchainIds.Ropsten => new ChainEstimations(
                    new LinearExtrapolation(35900.MB(), 25.MB(), new DateTime(2021, 12, 7))),
                BlockchainIds.Mainnet => new ChainEstimations(
                    new LinearExtrapolation(90000.MB(), 70.MB(), new DateTime(2022, 04, 7))),
                BlockchainIds.Gnosis => new ChainEstimations(
                    new LinearExtrapolation(18000.MB(), 48.MB(), new DateTime(2021, 12, 7))),
                BlockchainIds.EnergyWeb => new ChainEstimations(
                    new LinearExtrapolation(15300.MB(), 15.MB(), new DateTime(2021, 12, 7))),
                BlockchainIds.Volta => new ChainEstimations(
                    new LinearExtrapolation(17500.MB(), 10.MB(), new DateTime(2021, 11, 7))),
                BlockchainIds.PoaCore => new ChainEstimations(
                    new LinearExtrapolation(13900.MB(), 4.MB(), new DateTime(2021, 12, 7))),
                _ => UnknownChain.Instance
            };
        }
    }
}
