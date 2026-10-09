// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Memory;
using Nethermind.Int256;

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

    [ConfigItem(Description = $"Whether to reduce block latency by managing garbage collection around `engine_newPayload`: a collection after each block (see `{nameof(SweepMemory)}`), periodic decommit (see `{nameof(CollectionsPerDecommit)}`) and a no-GC region during block processing (see `{nameof(NoGcRegionOnNewPayload)}`). None of them runs while syncing or when this is `false`.", DefaultValue = "true")]
    public bool PrioritizeBlockLatency { get; set; }

    [ConfigItem(Description = $"When `engine_newPayload` enters a no-GC region for block processing. Entering pauses every thread on the payload's path and collects nothing; it only keeps a collection out of a block that would otherwise use up gen0's allocation budget, which the collection after each block refreshes. `{nameof(NoGcRegionMode.Guard)}` enters only when the estimated budget left is below the guard's threshold (see `{nameof(NoGcRegionGuardMb)}`). Has no effect unless `{nameof(PrioritizeBlockLatency)}` is `true`.", DefaultValue = nameof(NoGcRegionMode.Guard))]
    public NoGcRegionMode NoGcRegionOnNewPayload { get; set; }

    [ConfigItem(Description = $"The gen0 allocation budget, in MB, that has to be left for `engine_newPayload` to skip the no-GC region when `{nameof(NoGcRegionOnNewPayload)}` is `{nameof(NoGcRegionMode.Guard)}`. `0` derives it: the larger of 3/4 of gen0's budget and twice the most allocated during one `engine_newPayload` over the last 300-600 blocks, and at least 128 MB. A positive value overrides it.", DefaultValue = "0")]
    public int NoGcRegionGuardMb { get; set; }

    [ConfigItem(Description = "The garbage collection (GC) mode between Engine API calls.", DefaultValue = nameof(GcLevel.Gen1))]
    public GcLevel SweepMemory { get; set; }

    [ConfigItem(Description = $"The compaction mode for ordinary post-block collections; periodic decommit collections always fully compact. No requests non-blocking collection, which may be skipped during background GC and may increase steady-state memory usage. When set to `{nameof(GcCompaction.Full)}`, compacts the large object heap (LOH) if `{nameof(SweepMemory)}` is set to `{nameof(GcLevel.Gen2)}`.",
        DefaultValue = nameof(GcCompaction.No))]
    public GcCompaction CompactMemory { get; set; }

    [ConfigItem(Description = """
            The number of eligible newPayload calls between compacting collections that release process memory. Decommit waits for at least three seconds after payload completion (or PostBlockGcDelayMs, if longer); a new payload cancels the wait without clearing the count.

            Allowed values:

            - `-1`: No requests.
            - `0`: Requests every time.
            - A positive number: Requests after that many eligible newPayload calls, including calls whose entry was skipped or pending collection was cancelled. Calls made while the no-GC strategy is disabled (such as during sync) do not count.


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
}
