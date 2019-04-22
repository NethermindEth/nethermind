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
     public interface ITopicRadius
        {
            Topic topic { get; }
            byte[] topicHashPrefix { get; }

            ulong radius { get; set; }

            List<TopicRadiusBucket> buckets { get; set; }

            bool converged { get; }

            int radiusLookupCnt { get; set; }

            int getBucketIdx(Keccak addrHash);
            
            Keccak targetForBucket(int bucket);

            bool isInRadius(Keccak addrHash);

            int chooseLookupBucket(int a, int b);

            bool needMoreLookups(int a, int b, double maxValue);
            (ulong, int) recalcRadius();
            LookupInfo nextTarget(bool forceRegular);

            void adjustWithTicket(long now, Keccak targetHash, in TicketRef t);

            void adjust(long now, Keccak targetHash, Keccak addrHash, double inside);

        }}