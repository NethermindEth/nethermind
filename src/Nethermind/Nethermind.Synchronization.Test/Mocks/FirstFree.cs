// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Stats;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Peers.AllocationStrategies;

namespace Nethermind.Synchronization.Test.Mocks
{
    public class FirstFree : IPeerAllocationStrategy
    {
        private static FirstFree? _instance;

        public static FirstFree Instance
        {
            get
            {
                if (_instance is null) LazyInitializer.EnsureInitialized(ref _instance, static () => new FirstFree());

                return _instance;
            }
        }

        private FirstFree()
        {
        }

        public PeerInfo Allocate(PeerInfo? currentPeer, IEnumerable<PeerInfo> peers, INodeStatsManager nodeStatsManager, IBlockTree blockTree)
        {
            return peers.FirstOrDefault() ?? currentPeer!;
        }
    }
}
