// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

using Nethermind.Synchronization.ParallelSync;

namespace Nethermind.Facade.Eth
{
    [JsonConverter(typeof(SyncingResultJsonConverter))]
    [StructLayout(LayoutKind.Auto)]
    public struct SyncingResult
    {
        public static SyncingResult NotSyncing = new();
        public ulong StartingBlock { get; set; }
        public ulong CurrentBlock { get; set; }
        public ulong HighestBlock { get; set; }
        public bool IsSyncing { get; set; }
        public SyncMode SyncMode { get; set; }
        public ulong? WarpChunksAmount { get; set; }
        public ulong? WarpChunksProcessed { get; set; }
        public ulong? SyncedAccounts { get; set; }
        public ulong? SyncedStorage { get; set; }
        public ulong? SyncedBytecodes { get; set; }
        public ulong? HealedTrienodes { get; set; }
        public ulong? HealedBytecodes { get; set; }

        public override readonly string ToString() => $"IsSyncing: {IsSyncing}, StartingBlock: {StartingBlock}, CurrentBlock {CurrentBlock}, HighestBlock {HighestBlock}";
    }
}
