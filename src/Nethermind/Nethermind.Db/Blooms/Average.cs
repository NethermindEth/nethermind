// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
