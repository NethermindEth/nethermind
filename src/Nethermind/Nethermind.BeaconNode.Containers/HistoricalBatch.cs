using System.Collections.Generic;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.Containers
{
    public class HistoricalBatch
    {
        private readonly Hash32[] _blockRoots;
        private readonly Hash32[] _stateRoots;

        public HistoricalBatch(Hash32[] blockRoots, Hash32[] stateRoots)
        {
            _blockRoots = blockRoots;
            _stateRoots = stateRoots;
        }

        public IReadOnlyList<Hash32> BlockRoots { get { return _blockRoots; } }

        public IReadOnlyList<Hash32> StateRoots { get { return _stateRoots; } }
    }
}
