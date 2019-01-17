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
using Nethermind.Core.Crypto.Keccak256;

namespace Nethermind.Network.Discovery.RoutingTable
{
     public interface ITopicRadius
        {
            public Topic topic { get; }
            public byte[] topicHashPrefix { get; }

            public ulong radius { get; set; }

            public TopicRadiusBucket[] buckets;

            public bool converged;

            public int radiusLookupCnt;

            public TopicRadius(Topic t);

            public int getBucketIdx(Keccak addrHash);
            
            public Keccak targetForBucket(int bucket);

            public bool isInRadius(Keccak addrHash);

            public int chooseLookupBucket(int a, int b);

            public bool needMoreLooukps(int a, int b, double maxValue);
            public (ulong, int) recalcRadius();
            public LookupInfo nextTarget(bool forceRegular);

            public void adjustWithTicket(long now, Keccak targetHash, TicketRef t);

            public void adjust(long now, Keccak targetHash, Keccak addrHash, double inside);

        }
}