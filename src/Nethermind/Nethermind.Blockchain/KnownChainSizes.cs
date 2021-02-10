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
            {ChainId.Goerli,  new SizeInfo(3186.MB(), 9.MB(), new DateTime(2021, 01, 19))},
            {ChainId.Rinkeby,  new SizeInfo(13700.MB(), 8.MB(), new DateTime(2020, 4, 23))},
            {ChainId.Mainnet,  new SizeInfo(46000.MB(), 60.MB(), new DateTime(2020, 7, 20))},
            // {ChainId.PoaCore,  new SizeInfo(7660.MB(), 8.MB(), new DateTime(2020, 7, 20))},
            // {ChainId.Ropsten,  new SizeInfo(12000.MB(), 4.MB(), new DateTime(2020, 4, 23))},
        };
    }
}
