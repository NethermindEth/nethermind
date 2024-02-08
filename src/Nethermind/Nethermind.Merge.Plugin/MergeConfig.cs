// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Merge.Plugin.GC;

namespace Nethermind.Merge.Plugin
{
    public class MergeConfig : IMergeConfig
    {
        public bool Enabled { get; set; } = true;

        public string? FinalTotalDifficulty { get; set; }

        public string? TerminalTotalDifficulty { get; set; }

        public string? TerminalBlockHash { get; set; }

        public long? TerminalBlockNumber { get; set; }

        [Obsolete("Use BlocksConfig.SecondsPerSlot")]
        public ulong SecondsPerSlot { get; set; } = 12;

        public string? BuilderRelayUrl { get; set; }

        public bool PrioritizeBlockLatency { get; set; } = true;

        public GcLevel SweepMemory { get; set; } = GcLevel.Gen1;

        public GcCompaction CompactMemory { get; set; } = GcCompaction.Yes;

        public int CollectionsPerDecommit { get; set; } = 25;

        public int NewPayloadTimeout { get; set; } = 7;

        public bool SimulateBlockProduction { get; set; } = false;
    }
}
