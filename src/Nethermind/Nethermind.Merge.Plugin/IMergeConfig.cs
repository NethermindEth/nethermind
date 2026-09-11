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
    public ulong? TerminalBlockNumber { get; set; }

    [ConfigItem(Description = "Deprecated since v1.14.7. Use `Blocks.SecondsPerSlot` instead.", DefaultValue = "12", HiddenFromDocs = true)]
    public ulong SecondsPerSlot { get; set; }

    [ConfigItem(DisabledForCli = true, HiddenFromDocs = true)]
    Hash256 TerminalBlockHashParsed => string.IsNullOrWhiteSpace(TerminalBlockHash) ? Keccak.Zero : new Hash256(Bytes.FromHexString(TerminalBlockHash));

    [ConfigItem(Description = "The URL of a builder relay. If specified, blocks are sent to the relay.", DefaultValue = "null")]
    string? BuilderRelayUrl { get; set; }

    [ConfigItem(Description = "Whether to reduce block latency by disabling garbage collection during Engine API calls.", DefaultValue = "true")]
    public bool PrioritizeBlockLatency { get; set; }

    [ConfigItem(Description = "The garbage collection (GC) mode between Engine API calls.", DefaultValue = nameof(GcLevel.Gen1))]
    public GcLevel SweepMemory { get; set; }

    [ConfigItem(Description = $"The memory compaction mode. When set to `{nameof(GcCompaction.Full)}`, compacts the large object heap (LOH) if `{nameof(SweepMemory)}` is set to `{nameof(GcLevel.Gen2)}`.",
        DefaultValue = nameof(GcCompaction.Yes))]
    public GcCompaction CompactMemory { get; set; }

    [ConfigItem(Description = """
            The number of requests to the garbage collector (GC) to release the process memory.

            Allowed values:

            - `-1`: No requests.
            - `0`: Requests every time.
            - A positive number: Requests after that many Engine API calls.


            """, DefaultValue = "25")]
    public int CollectionsPerDecommit { get; set; }

    [ConfigItem(Description = "The timeout, in milliseconds, for the `engine_newPayload` method.", DefaultValue = "7000", HiddenFromDocs = true)]
    public int NewPayloadBlockProcessingTimeout { get; set; }

    [ConfigItem(Description = "Cache NewPayload valid or invalid results", DefaultValue = "50", HiddenFromDocs = true)]
    public int NewPayloadCacheSize { get; }

    [ConfigItem(Description = "[TECHNICAL] Simulate block production for every possible slot. Just for stress-testing purposes.", DefaultValue = "false", HiddenFromDocs = true)]
    bool SimulateBlockProduction { get; set; }

    [ConfigItem(Description = "Delay, in milliseconds, between `newPayload` and GC trigger. If not set, defaults to 1/8th of `Blocks.SecondsPerSlot`.", DefaultValue = null, HiddenFromDocs = true)]
    int? PostBlockGcDelayMs { get; set; }

    [ConfigItem(Description = """
            [EXPERIMENTAL] The share, from `0` to `1`, of an EIP-7805 inclusion list drawn from the longest-pending senders. `0` draws the whole list uniformly over the transaction pool.

            A floor rather than a quota: the cohort gets this share or the share a uniform draw would have given it, whichever is larger, so a share below `InclusionListOldestSenderCount` divided by the pool size changes nothing.

            A non-zero share raises the odds that a transaction a builder keeps passing over reaches a list, but it also weakens the list against a flooded pool, since pending age costs an attacker nothing but time. Age is measured from when the pool accepted the sender's next pending transaction, so replacing it — a fee bump, for instance — restamps it as newly arrived and drops the sender out of the cohort. Measure before enabling it on a live network.
            """, DefaultValue = "0", HiddenFromDocs = true)]
    double InclusionListOldestSenderShare { get; set; }

    [ConfigItem(Description = """
            [EXPERIMENTAL] How many of the longest-pending senders `InclusionListOldestSenderShare` samples from. Ignored when that share is `0`, and pools no larger than this are drawn uniformly.

            It sets both sides of the trade at once: it is what an attacker must outbid to crowd the cohort out, and it is the cohort's weight in the pool, which is the share below which the knob does nothing. Keep it well above the share's part of the 256 senders a list draws, or every committee member samples the same fixed set of senders.
            """, DefaultValue = "200", HiddenFromDocs = true)]
    int InclusionListOldestSenderCount { get; set; }
}
