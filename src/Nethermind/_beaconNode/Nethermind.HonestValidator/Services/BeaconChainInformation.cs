//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System.Threading.Tasks;
using Nethermind.Core2.Api;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Types;

namespace Nethermind.HonestValidator.Services
{
    public class BeaconChainInformation
    {
        public Fork Fork { get; private set; } = Fork.Zero;
        public ulong GenesisTime { get; private set; }

        public Slot LastAggregationSlotChecked { get; private set; }

        public Slot LastAttestationSlotChecked { get; private set; }

        public Slot LastStartSlotChecked { get; private set; }

        public bool NodeIsSyncing { get; private set; }

        public SyncingStatus SyncStatus { get; private set; } = SyncingStatus.Zero;

        public ulong Time { get; private set; }

        public Task SetForkAsync(Fork fork)
        {
            Fork = fork;
            return Task.CompletedTask;
        }

        public Task SetGenesisTimeAsync(ulong time)
        {
            GenesisTime = time;
            return Task.CompletedTask;
        }

        public Task SetLastAggregationSlotChecked(Slot slot)
        {
            LastAggregationSlotChecked = slot;
            return Task.CompletedTask;
        }

        public Task SetLastAttestationSlotChecked(Slot slot)
        {
            LastAttestationSlotChecked = slot;
            return Task.CompletedTask;
        }

        public Task SetLastStartSlotChecked(Slot slot)
        {
            LastStartSlotChecked = slot;
            return Task.CompletedTask;
        }

        public Task SetSyncStatus(Syncing syncing)
        {
            NodeIsSyncing = syncing.IsSyncing;
            if (syncing.SyncStatus != null)
            {
                SyncStatus = syncing.SyncStatus;
            }

            return Task.CompletedTask;
        }

        public Task SetTimeAsync(ulong time)
        {
            Time = time;
            return Task.CompletedTask;
        }
    }
}