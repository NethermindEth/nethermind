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
                if (_instance is null) LazyInitializer.EnsureInitialized(ref _instance, () => new FirstFree(false));

                return _instance;
            }
        }


        private static FirstFree? _replaceableInstance;
        public static FirstFree ReplaceableInstance
        {
            get
            {
                if (_replaceableInstance is null) LazyInitializer.EnsureInitialized(ref _replaceableInstance, () => new FirstFree(true));

                return _replaceableInstance;
            }
        }

        private FirstFree(bool canBeReplaced)
        {
            CanBeReplaced = canBeReplaced;
        }

        public bool CanBeReplaced { get; private set; }

        public PeerInfo Allocate(PeerInfo? currentPeer, IEnumerable<PeerInfo> peers, INodeStatsManager nodeStatsManager, IBlockTree blockTree)
        {
            return peers.FirstOrDefault() ?? currentPeer!;
        }
    }
}
