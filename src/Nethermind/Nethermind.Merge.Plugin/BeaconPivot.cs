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
// 

using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Synchronization;

namespace Nethermind.Merge.Plugin
{
    public class BeaconPivot : IBeaconPivot
    {
        private readonly IBlockTree _blockTree;
        private BlockHeader? _currentPivot;
        private BlockHeader? _pivotParent;
        private bool _pivotParentProcessed;
        

        public BeaconPivot(IBlockTree blockTree)
        {
            _blockTree = blockTree;
        }

        public long? PivotNumber => _currentPivot?.Number;

        public Keccak? PivotHash => _currentPivot?.Hash;

        public UInt256? PivotTotalDifficulty => _currentPivot?.TotalDifficulty;

        public void EnsurePivot(BlockHeader? blockHeader)
        {
            if (_currentPivot == null)
                _currentPivot = blockHeader;
        }

        public bool IsPivotParentParentProcessed()
        {
                EnsurePivotParentProcessed();
                return _pivotParentProcessed;
        }

        private void EnsurePivotParentProcessed()
        {
            if (_pivotParentProcessed || _currentPivot == null)
                return;
            
            if (_pivotParent == null)
                _pivotParent = _blockTree.FindParentHeader(_currentPivot!,
                    BlockTreeLookupOptions.TotalDifficultyNotNeeded);
            
            if (_pivotParent != null)
                _pivotParentProcessed = _blockTree.WasProcessed(_pivotParent.Number,
                    _pivotParent.Hash ?? _pivotParent.CalculateHash());
        }
    }

    public interface IBeaconPivot : IPivot
    {
        bool IsPivotParentParentProcessed();

        void EnsurePivot(BlockHeader? blockHeader);
    }
}
