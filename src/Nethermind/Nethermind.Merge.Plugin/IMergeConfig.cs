// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Merge.Plugin.GC;

namespace Nethermind.Merge.Plugin
{
    public interface IMergeConfig : IConfig
    {
        [ConfigItem(
            Description = "Defines whether the Merge plugin is enabled bundles are allowed.",
            DefaultValue = "true")]
        bool Enabled { get; set; }

        [ConfigItem(Description = "Final total difficulty is total difficulty of the last PoW block. FinalTotalDifficulty >= TerminalTotalDifficulty.", DefaultValue = "null")]
        public string? FinalTotalDifficulty { get; set; }

        [ConfigItem(DisabledForCli = true, HiddenFromDocs = true, DefaultValue = "null")]
        UInt256? FinalTotalDifficultyParsed => string.IsNullOrWhiteSpace(FinalTotalDifficulty) ? null : UInt256.Parse(FinalTotalDifficulty);

        [ConfigItem(Description = "Terminal total difficulty used for transition process.", DefaultValue = "null")]
        public string? TerminalTotalDifficulty { get; set; }

        [ConfigItem(DisabledForCli = true, HiddenFromDocs = true, DefaultValue = "null")]
        UInt256? TerminalTotalDifficultyParsed => string.IsNullOrWhiteSpace(TerminalTotalDifficulty) ? null : UInt256.Parse(TerminalTotalDifficulty);

        [ConfigItem(Description = "Terminal PoW block hash used for transition process.", DefaultValue = "null")]
        public string? TerminalBlockHash { get; set; }

        [ConfigItem(Description = "Terminal PoW block number used for transition process.")]
        public long? TerminalBlockNumber { get; set; }

        [ConfigItem(Description = "Deprecated since v1.14.7. Please use Blocks.SecondsPerSlot. " +
            "Seconds per slot.", DefaultValue = "12")]
        public ulong SecondsPerSlot { get; set; }

        [ConfigItem(DisabledForCli = true, HiddenFromDocs = true)]
        Keccak TerminalBlockHashParsed => string.IsNullOrWhiteSpace(TerminalBlockHash) ? Keccak.Zero : new Keccak(Bytes.FromHexString(TerminalBlockHash));

        [ConfigItem(Description = "URL to Builder Relay. If set when building blocks nethermind will send them to the relay.", DefaultValue = "null")]
        string? BuilderRelayUrl { get; set; }

        [ConfigItem(Description = "Reduces block EngineApi latency by disabling Garbage Collection during EngineApi calls.", DefaultValue = "true")]
        public bool PrioritizeBlockLatency { get; set; }

        [ConfigItem(Description = "Reduces memory usage by forcing Garbage Collection between EngineApi calls. Accept values `NoGc` (-1), Gen0 (0), Gen1 (1), Gen2 (2).", DefaultValue = "Gen1")]
        public GcLevel SweepMemory { get; set; }

        [ConfigItem(Description = "Reduces process used memory. Accept values `No` which disables it, `Yes` which compacts normal heaps, `Full` compacts large object heap too (only when SweepMemory is set to `Gen2`).", DefaultValue = "Yes")]
        public GcCompaction CompactMemory { get; set; }

        [ConfigItem(Description = "Requests the GC to release process memory back to OS. Accept values `-1` which disables it, `0` which releases every time, and any positive integer which does it after that many EngineApi calls.", DefaultValue = "25")]
        public int CollectionsPerDecommit { get; set; }
    }
}
