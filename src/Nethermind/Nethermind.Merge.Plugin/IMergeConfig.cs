// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Merge.Plugin.GC;

namespace Nethermind.Merge.Plugin;

public interface IMergeConfig : IConfig
{
    [ConfigItem(Description = "Whether to enable the Merge hard fork.", DefaultValue = "true")]
    bool Enabled { get; set; }

    [ConfigItem(Description = "The total difficulty of the last PoW block. Must be greater than or equal to the terminal total difficulty (TTD).", DefaultValue = "null")]
    public string? FinalTotalDifficulty { get; set; }

    [ConfigItem(DisabledForCli = true, HiddenFromDocs = true, DefaultValue = "null")]
    UInt256? FinalTotalDifficultyParsed => string.IsNullOrWhiteSpace(FinalTotalDifficulty) ? null : UInt256.Parse(FinalTotalDifficulty);

    [ConfigItem(Description = "The terminal total difficulty (TTD) used for the transition.", DefaultValue = "null")]
    public string? TerminalTotalDifficulty { get; set; }

    [ConfigItem(DisabledForCli = true, HiddenFromDocs = true, DefaultValue = "null")]
    UInt256? TerminalTotalDifficultyParsed => string.IsNullOrWhiteSpace(TerminalTotalDifficulty) ? null : UInt256.Parse(TerminalTotalDifficulty);

    [ConfigItem(Description = "The terminal PoW block hash used for the transition.", DefaultValue = "null")]
    public string? TerminalBlockHash { get; set; }

    [ConfigItem(Description = "The terminal PoW block number used for the transition.")]
    public long? TerminalBlockNumber { get; set; }

    [ConfigItem(Description = "Deprecated since v1.14.7. Use `Blocks.SecondsPerSlot` instead.", DefaultValue = "12", HiddenFromDocs = true)]
    public ulong SecondsPerSlot { get; set; }

    [ConfigItem(DisabledForCli = true, HiddenFromDocs = true)]
    Hash256 TerminalBlockHashParsed => string.IsNullOrWhiteSpace(TerminalBlockHash) ? Keccak.Zero : new Hash256(Bytes.FromHexString(TerminalBlockHash));

    [ConfigItem(Description = "The URL of a builder relay. If specified, blocks are sent to the relay.", DefaultValue = "null")]
    string? BuilderRelayUrl { get; set; }

    [ConfigItem(Description = "Whether to reduce block latency by disabling garbage collection during Engine API calls.", DefaultValue = "true")]
    public bool PrioritizeBlockLatency { get; set; }

    [ConfigItem(Description = "The garbage collection (GC) mode between Engine API calls.", DefaultValue = "Gen1")]
    public GcLevel SweepMemory { get; set; }

    [ConfigItem(Description = "The memory compaction mode. When set to `Full`, compacts the large object heap (LOH) if `SweepMemory` is set to `Gen2`.", DefaultValue = "Yes")]
    public GcCompaction CompactMemory { get; set; }

    [ConfigItem(Description = """
            Request the garbage collector (GC) to release the process memory.

            Allowed values:

            - `-1` to disable
            - `0` to release every time
            - A positive number to release memory after that many Engine API calls


            """, DefaultValue = "75")]
    public int CollectionsPerDecommit { get; set; }

    [ConfigItem(Description = "The timeout, in seconds, for the `engine_newPayload` method.", DefaultValue = "7", HiddenFromDocs = true)]
    public int NewPayloadTimeout { get; }

    [ConfigItem(Description = "[TECHNICAL] Simulate block production for every possible slot. Just for stress-testing purposes.", DefaultValue = "false", HiddenFromDocs = true)]
    bool SimulateBlockProduction { get; set; }
}
