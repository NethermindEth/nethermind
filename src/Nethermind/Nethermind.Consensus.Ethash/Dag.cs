//  Copyright (c) 2020 Andrea Lanfranchi
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
using System.Text;

namespace Nethermind.Consensus.Ethash
{
    public class Dag
    {

        public enum DeployModeEnum
        {
            Light = 0,  // Allocates light cache only (default)
            Lazy = 1,   // Allocates light cache + data (data is populated dynamically)
            Full = 2    // Allocates light cache + data (data is fully unrolled)
        };

        public uint DataInitSize { get; }    // Initial size of Dag Dataset at epoch 0
        public uint DataGrowthSize { get; }  // Increase size multiplier for each subsequent epoch
        public uint DataItemSize { get; }    // Size of each data item
        public uint DataItemParents { get; } // Number of light cache accesses for each data item
        public uint CacheInitSize { get; }   // Initial size of light cache at epoch 0
        public uint CacheGrowthSize { get; } // Increase size multiplier for each subsequent epoch
        public uint CacheItemSize { get; }   // Size of each data item
        public uint CacheRounds { get; }     // Light cache rounds for every unrolled data item

        public uint DataItems { get; private set; } = 0;   // Number of items included in the unrolled dataset
        public uint CacheItems { get; private set; } = 0;  // Number of items included in the unrolled cacheset

        public ulong CacheSize { get { return CacheItems * CacheItemSize; } }  // Size in bytes of the unrolled cacheset
        public ulong DataSize { get { return DataItems * DataItemSize; } }     // Size in bytes of the unrolled dataset

        public uint? Epoch { get; private set; } = null;
        public bool Deployed { get; private set; } = false;

        public Dag(string specName = "ethash")
        {

            // Default specs for Ethash (aka DAG revision 23)
            if (specName == "ethash")
            {
                DataInitSize = 1U << 30;
                DataGrowthSize = 1U << 23;
                DataItemSize = 1U << 10;
                DataItemParents = 256;
                CacheInitSize = 1U << 24;
                CacheGrowthSize = 1U << 17;
                CacheItemSize = 1U << 9;
                CacheRounds = 3;
            }
            else
            {
                throw new NotImplementedException($"specName == {specName} not implemented yet");
            }

            DoMath();
        }

        private void DoMath()
        {
            if (CacheInitSize % CacheItemSize != 0) throw new InvalidOperationException($"CacheInitSize not a multiple of CacheItemSize");
            if (CacheGrowthSize % CacheItemSize != 0) throw new InvalidOperationException($"CacheGrowthSize not a multiple of CacheItemSize");
            if (CacheInitSize > DataInitSize) throw new InvalidOperationException($"CacheInitSize greater than DataInitSize");
            if (DataInitSize % DataItemSize != 0) throw new InvalidOperationException($"DataInitSize not a multiple of DataItemSize");
            if (DataGrowthSize % DataItemSize != 0) throw new InvalidOperationException($"DataGrowthSize not a multiple of DataItemSize");
            if (Epoch is null) return;

            // Compute number of cache items
            uint cacheInitItems = CacheInitSize / CacheItemSize;
            uint cacheGrowthItems = CacheGrowthSize / CacheItemSize;
            uint cacheItemsUpper = cacheInitItems + cacheGrowthItems * Epoch.GetValueOrDefault(0);

            // Compute number of data items
            uint dataInitItems = DataInitSize / DataItemSize;
            uint dataGrowthItems = DataGrowthSize / DataItemSize;
            uint dataItemsUpper = dataInitItems + dataGrowthItems * Epoch.GetValueOrDefault(0);

        }
    }
}
