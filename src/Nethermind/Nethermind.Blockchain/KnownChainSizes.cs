//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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
            {ChainId.Goerli,  new SizeInfo(8490.MB(), 15.MB(), new DateTime(2021, 12, 7))},
            {ChainId.Rinkeby,  new SizeInfo(34700.MB(), 20.MB(), new DateTime(2021, 12, 7))},
            {ChainId.Ropsten,  new SizeInfo(35900.MB(), 25.MB(), new DateTime(2021, 12, 7))},
            {ChainId.Mainnet,  new SizeInfo(90000.MB(), 70.MB(), new DateTime(2022, 04, 7))},
            {ChainId.xDai,  new SizeInfo(18000.MB(), 48.MB(), new DateTime(2021, 12, 7))},
            {ChainId.EnergyWeb,  new SizeInfo(15300.MB(), 15.MB(), new DateTime(2021, 12, 7))},
            {ChainId.Volta,  new SizeInfo(17500.MB(), 10.MB(), new DateTime(2021, 11, 7))},
            {ChainId.PoaCore,  new SizeInfo(13900.MB(), 4.MB(), new DateTime(2021, 12, 7))},
        };
    }
}
