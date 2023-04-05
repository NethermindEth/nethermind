// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Extensions;

namespace Nethermind.Blockchain
{
    public interface ISizeInfo
    {
        long? CurrentSize { get; }
    }

    public static class ChainSizes
    {
        public class UnknownChain : ISizeInfo
        {
            public long? CurrentSize => null;

            public static readonly ISizeInfo Instance = new UnknownChain();
        }

        private class SizeInfo : ISizeInfo
        {
            public SizeInfo(
                long sizeAtUpdateDate,
                long dailyGrowth,
                DateTime updateDate)
            {
                SizeAtUpdateDate = sizeAtUpdateDate;
                DailyGrowth = dailyGrowth;
                UpdateDate = updateDate;
            }

            public long SizeAtUpdateDate { get; }
            public long DailyGrowth { get; }
            public DateTime UpdateDate { get; }

            public long? CurrentSize => SizeAtUpdateDate + (DateTime.UtcNow - UpdateDate).Days * DailyGrowth;
        }

        /// <summary>
        /// Size in bytes, daily growth rate and the date of manual update
        /// </summary>
        public static ISizeInfo CreateChainSizeInfo(ulong chainId)
        {
            return chainId switch
            {
                BlockchainIds.Goerli => new SizeInfo(8490.MB(), 15.MB(), new DateTime(2021, 12, 7)),
                BlockchainIds.Rinkeby => new SizeInfo(34700.MB(), 20.MB(), new DateTime(2021, 12, 7)),
                BlockchainIds.Ropsten => new SizeInfo(35900.MB(), 25.MB(), new DateTime(2021, 12, 7)),
                BlockchainIds.Mainnet => new SizeInfo(90000.MB(), 70.MB(), new DateTime(2022, 04, 7)),
                BlockchainIds.Gnosis => new SizeInfo(18000.MB(), 48.MB(), new DateTime(2021, 12, 7)),
                BlockchainIds.EnergyWeb => new SizeInfo(15300.MB(), 15.MB(), new DateTime(2021, 12, 7)),
                BlockchainIds.Volta => new SizeInfo(17500.MB(), 10.MB(), new DateTime(2021, 11, 7)),
                BlockchainIds.PoaCore => new SizeInfo(13900.MB(), 4.MB(), new DateTime(2021, 12, 7)),
                _ => UnknownChain.Instance
            };
        }
    }
}
