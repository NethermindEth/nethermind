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
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Logging;
using Nethermind.Core.Model;
using Nethermind.KeyStore;
using Nethermind.Network.Config;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Network;

namespace Nethermind.Network.Discovery.RoutingTable
{
     public class TopicRadius : ITopicRadius
        {

            private readonly int _maxNoAdjust = 20;
            private readonly int _minPeakSize = 40;

            private readonly int _minRightSum = 20;

            private readonly TimeSpan _targetWaitTime = new TimeSpan(0, 10, 0);

            private Stopwatch _timestamp = new Stopwatch();

            public Topic topic { get; }
            public byte[] topicHashPrefix { get; }

            public ulong _maxRadius = 0xffffffffffffffff;
            public ulong radius { get; set; }

            public ulong _minRadius {get; set; }

            public TopicRadiusBucket[] buckets { get; set; }

            public bool converged { get; private set; }

            public int radiusLookupCnt { get; set; }

            private int _radiusBucketsPerBit = 8;
            
            private Random RandomNumberGenerator = new System.Random();

            private static readonly ICryptoRandom CryptoRandom = new CryptoRandom(); // Need this over RandomNumberGenerator for GetRandomBytes?

            public TopicRadius(Topic t)
            {
                topic = t;
                Keccak topicHash = Keccak.Compute(t.ToString());
                topicHashPrefix = topicHash.Bytes.Slice(0, 8);
                radius = _maxRadius;
                _minRadius = _maxRadius;
            }

            public int getBucketIdx(Keccak addrHash) {
                byte[] prefix = addrHash.Bytes.Slice(0, 8);
                double log2;
                if (prefix != topicHashPrefix) {
                    log2 = Math.Log( Math.Pow(BitConverter.ToDouble(prefix, 0), BitConverter.ToDouble(topicHashPrefix)), 2);
                }
                int bucket = (int)((64 - log2) * _radiusBucketsPerBit);
                int max = 64 * _radiusBucketsPerBit - 1;
                if (bucket > max) {
                    return max;
                }
                if (bucket < 0) {
                    return 0;
                }
                return bucket;
            }
            
            public Keccak targetForBucket(int bucket) {
                double min = Math.Pow(2, 64-Double(bucket+1)/_radiusBucketsPerBit);
                double max = Math.Pow(2, 64-Double(bucket)/_radiusBucketsPerBit);
                ulong a = (ulong)(min);
                
                ulong b = RandomNumberGenerator.Next(0, (ulong)(max-min));
                var xor = a + b;
                if (xor < a) {
                    xor = ~(ulong)(0);
                }
                byte[] prefix = topicHashPrefix ^ xor;
                Keccak target;

                target = new Keccak(prefix.Slice(0,8).Concat(CryptoRandom.GenerateRandomBytes(target.Length - 8)));
                return target;
            }

            public bool isInRadius(Keccak addrHash) {
                ulong nodePrefix = BitConverter.ToUInt64(addrHash.Bytes.Slice(0,8));
                ulong dist = nodePrefix ^ BitConverter.ToUInt64(topicHashPrefix);
                return dist < radius;
            }

            public int chooseLookupBucket(int a, int b) {
                if (a < 0 ){
                    a =0;
                }
                if (a > b) {
                    return -1;
                }
                int c = 0;
                for (int i = a; i <= b; i++) {
                    if ( i >= buckets.Length || buckets[i].weights[buckets[i].TopicRadiusEvent.trNoAdjust] < _maxNoAdjust) {
                        c++;
                    }
                }
                uint rnd = (uint)RandomNumberGenerator.Next(0, c);
                for (i = a; i <= b; i++) {
                    if ( i>= buckets.Length || buckets[i].weights[buckets[i].TopicRadiusEvent.trNoAdjust] < _maxNoAdjust) {
                        if (rnd == 0) {
                            return i;
                        }
                        rnd--;
                    }
                }
                _logger.Trace("Unexpected branch - exiting"); // should never happen
            }

            public bool needMoreLooukps(int a, int b, double maxValue) {
                double max;
                if (a < 0) {
                    a = 0;
                }
                if (b >= buckets.Length) {
                    b = buckets.Length - 1;
                    if (buckets[b].value > max) {
                        max = buckets[b].value;
                    }
                }
                if (b >= a) {
                    for (int i = a; i <= b; i++) {
                        if (buckets[i].value > max) {
                            max = buckets[i].value;
                        }
                    }
                }
                return maxValue-max < _minPeakSize;
            }

            public (ulong, int) recalcRadius() {
                int maxBucket = 0;
                double maxValue = 0;

                long now = _timestamp.GetTimestamp();

                double v = 0;

                for (int i = 0; i <= buckets.Length; i++) {
                    buckets[i].update(now);
                    v += buckets[i].weights[buckets[i].TopicRadiusEvent.trOutside] - buckets[i].weights[buckets[i].TopicRadiusEvent.trInside];
                    buckets[i].value = v;
                }
                //fmt.println();
                int slopeCross = -1;
                TopicRadiusBucket b;
                for (int i = 0; i < buckets.Length; i++) {
                    b = buckets[i];
                    v = b.value;
                    if (v < Double(i)*minSlope) {
                        slopeCross = i;
                        break;
                    }
                    if (v > maxValue) {
                        maxValue = v;
                        maxBucket = i + 1;
                    }
                }

                int minRadBucket = buckets.Length;
                double sum = 0;
                while (minRadBucket > 0 && sum < _minRightSum) {
                    minRadBucket--;
                    b = buckets[minRadBucket];
                    sum += b.weights[b.TopicRadiusEvent.trInside] + b.weights[b.TopicRadiusEvent.trOutside];
                }
                _minRadius = Math.Pow(2, 64-Double(minRadBucket)/_radiusBucketsPerBit).ToUInt64();

                int lookupLeft = -1;
                if (needMoreLookups(0, maxBucket-looukpWidth-1, maxValue)) {
                    lookupLeft = chooseLookupBucket(maxBucket - lookupWidth, maxBucket-1);
                }
                int lookupRight = -1;
                if (slopeCross != maxBucket && (minRadBucket <= maxBucket || needMoreLookups(maxBucket+lookupWidth, buckets.Length-1, maxValue))) {
                    while (buckets.Length <= maxBucket + lookupWidth) {
                        buckets.Append(new TopicRadiusBucket(new Dictionary<Keccak, long>()));
                    }
                    lookupRight = chooseLookupBucket(maxBucket, maxBucket+lookupWidth+1);
                }
                if (lookupLeft == -1) {
                    radiusLookup = lookupRight;
                } else {
                    if (lookupRight == -1) {
                        radiusLookup = lookupLeft;
                    } else {
                        if (RandomNumberGenerator.Next(0, 2) == 0) {
                            radiusLookup = lookupLeft;
                        } else {
                            radiusLookup = lookupRight;
                        }

                    }
                }

                	//fmt.Println("mb", maxBucket, "sc", slopeCross, "mrb", minRadBucket, "ll", lookupLeft, "lr", lookupRight, "mv", maxValue)

                if (radiusLookup == -1) {
                    // no more radius lookups needed at hte moment, return a radius
                    converged = true;
                    int rad = maxBucket;
                    if (minRadBucket < rad) {
                        rad = minRadBucket;
                    }
                    ulong newRadius = ~ulong.Parse("0");
                    if (rad > 0) {
                        newRadius = Math.Pow(2, 64-Double(rad)/_radiusBucketsPerBit ).ToUInt64();
                    }
                    radius = newRadius;
                }

                return (radius, radiusLookup);
            }

            public LookupInfo nextTarget(bool forceRegular) {
                if (!forceRegular) {
                    int radiusLookup = recalcRadius().Item2;
                    if (radiusLookup != -1) {
                        Keccak target = targetForBucket(radiusLookup);
                        buckets[radiusLookup].lookupSent.target = _timestamp.GetTimestamp();
                        return new LookupInfo(target, topic, true);
                    }
                }

                double radExt = radius / 2;
                if (radExt > _maxRadius-radius) {
                    radExt = _maxRadius - radius;
                }
                ulong rnd = (ulong)RandomNumberGenerator.Next(0, radius) + (ulong)RandomNumberGenerator.Next(0, 2*radExt);
                if (rnd > radExt) {
                    rnd -= radExt;
                } else {
                    rnd = radExt - rnd;
                }

                ulong prefix = BitConverter.ToUInt64(topicHashPrefix, 0) ^ rnd;
                Keccak target;
                target = new Keccak(prefix.Slice(0,8).Concat(CryptoRandom.GenerateRandomBytes(target.Length - 8)));
                return new LookupInfo(target, topic, true);
            }

            public void adjustWithTicket(long now, Keccak targetHash, in TicketRef t) {
                long wait = t.t.regTime[t.idx] - t.t.issueTime;
                double inside = Double(wait)/Double(_targetWaitTime.TotalMilliseconds*1000000) - 0.5;
                if (inside > 1) {
                    inside = 1;
                }
                if (inside < 0) {
                    inside = 0;
                }
                adjust(now, targetHash, t.t.node.sha, inside);
            }

            public void adjust(long now, Keccak targetHash, Keccak addrHash, double inside) {
                int bucket = getBucketIdx(addrHash);
                _logger.Trace($"adjust\n{bucket}\n{buckets.Length}\n{inside}");
                if (bucket >= buckets.Length) {
                    return;
                }
                buckets[bucket].adjust(now, inside);
                buckets[bucket].lookupSent.Remove(targetHash);
            }
            /*
            public bool Equals(TopicRadius other)
            {
                return string.Equals(topic.ToString(), other.topic.ToSTring());
            }

            public override bool Equals(object obj)
            {
                if (ReferenceEquals(null, obj)) return false;
                return obj is TopicRadius && Equals((TopicRadius)obj);
            }
            */
        }
}