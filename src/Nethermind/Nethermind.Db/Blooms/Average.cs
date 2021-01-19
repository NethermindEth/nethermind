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

using System.Collections.Generic;

namespace Nethermind.Db.Blooms
{
    public class Average
    {
        public decimal Value
        {
            get
            {
                decimal sum = 0;
                uint count = 0;
                
                foreach (var bucket in Buckets)
                {
                    sum += bucket.Key * bucket.Value;
                    count += bucket.Value;
                }

                return count == 0 ? 0 : sum / count;
            }
        }

        public readonly IDictionary<uint, uint> Buckets = new Dictionary<uint, uint>();
        
        public int Count { get; private set; }

        public void Increment(uint value)
        {
            Buckets[value] = Buckets.TryGetValue(value, out var count) ? count + 1 : 1;
            Count++;
        }
    }
}
