// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Extensions;

namespace Nethermind.Blockchain
{
    public static class Known
    {
        public readonly struct SizeInfo
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

            public long Current => SizeAtUpdateDate + (DateTime.UtcNow - UpdateDate).Days * DailyGrowth;
        }

        /// <summary>
        /// Size in bytes, daily growth rate and the date of manual update
        /// </summary>
        public static Dictionary<ulong, SizeInfo> ChainSize = new()
        {
            { ChainId.Goerli, new SizeInfo(8490.MB(), 15.MB(), new DateTime(2021, 12, 7)) },
            { ChainId.Rinkeby, new SizeInfo(34700.MB(), 20.MB(), new DateTime(2021, 12, 7)) },
            { ChainId.Ropsten, new SizeInfo(35900.MB(), 25.MB(), new DateTime(2021, 12, 7)) },
            { ChainId.Mainnet, new SizeInfo(90000.MB(), 70.MB(), new DateTime(2022, 04, 7)) },
            { ChainId.xDai, new SizeInfo(18000.MB(), 48.MB(), new DateTime(2021, 12, 7)) },
            { ChainId.EnergyWeb, new SizeInfo(15300.MB(), 15.MB(), new DateTime(2021, 12, 7)) },
            { ChainId.Volta, new SizeInfo(17500.MB(), 10.MB(), new DateTime(2021, 11, 7)) },
            { ChainId.PoaCore, new SizeInfo(13900.MB(), 4.MB(), new DateTime(2021, 12, 7)) },
        };
    }
}
