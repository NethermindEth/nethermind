// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
