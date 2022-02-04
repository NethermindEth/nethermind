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
using Nethermind.Synchronization;
using Nethermind.Synchronization.ParallelSync;

namespace Nethermind.Merge.Plugin.Synchronization
{
    public class BeaconSync : IMergeSyncController, IBeaconSyncStrategy
    {
        private readonly IBeaconPivot _beaconPivot;
        private readonly IBlockTree _blockTree;
        private readonly ISyncProgressResolver _syncProgressResolver;
        private bool _isInBeaconModeControl = false;

        public BeaconSync(
            IBeaconPivot beaconPivot,
            IBlockTree blockTree,
            ISyncProgressResolver syncProgressResolver)
        {
            _beaconPivot = beaconPivot;
            _blockTree = blockTree;
            _syncProgressResolver = syncProgressResolver;
        }

        public void SwitchToBeaconModeControl()
        {
            _isInBeaconModeControl = true;
        }

        public void InitSyncing()
        {
            _isInBeaconModeControl = false;
        }

        public bool ShouldBeInBeaconHeaders()
        {
            bool beaconPivotExists =  _beaconPivot.BeaconPivotExists();
            bool notInBeaconModeControl = !_isInBeaconModeControl;
            bool notFinishedBeaconHeaderSync = !IsBeaconSyncHeadersFinished();

            return beaconPivotExists &&
                   notInBeaconModeControl &&
                   notFinishedBeaconHeaderSync;
        }

        public bool ShouldBeInBeaconModeControl() => _isInBeaconModeControl;
        
        public bool IsBeaconSyncHeadersFinished() => (_blockTree.LowestInsertedBeaconHeader?.Number ??
            _beaconPivot.PivotNumber) <= _beaconPivot.PivotDestinationNumber;
    }

    public interface IMergeSyncController
    {
        void SwitchToBeaconModeControl();

        void InitSyncing();
    }
}
