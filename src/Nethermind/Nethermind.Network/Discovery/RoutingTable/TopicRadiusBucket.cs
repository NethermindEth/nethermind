    /*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Diagnostics;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.Core.Model;
using Nethermind.KeyStore;
using Nethermind.Network.Config;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Network;

namespace Nethermind.Network.Discovery.RoutingTable
{
     public class TopicRadiusBucket : ITopicRadiusBucket
        {
            public enum TopicRadiusEvent { trOutside, trInside, trNoAdjust };

            public int trCount { get; private set; }

            public double[] weights { get; private set; }
            public long lastTime { get; set; }

            public double value { get; set; }

            public Dictionary<Keccak, long> lookupSent { get; set; } 

            private long _radiusTC = (long)new TimeSpan(0, 0, 20).TotalMilliseconds;

            private long _responseTimeout = (long)new TimeSpan(0, 0, 1).TotalMilliseconds/2;

            public TopicRadiusBucket()
            {
                trCount = Enum.GetNames(typeof(TopicRadiusEvent)).Length;
                weights = new double[trCount];
                lookupSent = new Dictionary<Keccak, long>();
            }

            public void update(long now) { // TODO: Convert to MonotonicTimestamp, a Long of DateTime.Ticks just typedefed
                if (new TimeSpan(now-lastTime).TotalMilliseconds == 0) {
                    return;
                }

                double exp = Math.Exp( ( (double)0 - (double)(now - lastTime) ) / (double)(_radiusTC));
                for (int i = 0; i < weights.Length; i++) {
                    weights[i] = weights[i] * exp;
                }
                lastTime = Stopwatch.GetTimestamp();

                for (int i = 0; i < lookupSent.Count; i++) {
                    Keccak target = lookupSent.Keys.ElementAt(i);
                    long tm = lookupSent.Values.ElementAt(i); // TODO: Convert to MonotonicTimestamp, a Long of DateTime.Ticks just typedefed
                    if (new TimeSpan(now-tm).TotalMilliseconds > _responseTimeout) {
                        weights[(int)TopicRadiusEvent.trNoAdjust] += 1;
                        lookupSent.Remove(target);
                    }
                }
            }

            public void adjust(long now, double inside) {
                update(now);
                if (inside <= 0) {
                    weights[(int)TopicRadiusEvent.trOutside] += 1;
                } else {
                    if (inside >= 1) {
                        weights[(int)TopicRadiusEvent.trInside] += 1;
                    } else {
                        weights[(int)TopicRadiusEvent.trInside] += inside;
                        weights[(int)TopicRadiusEvent.trOutside] += 1 - inside;
                    }
                }
            }
            
        }
}