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

    [ConfigItem(Description = "Whether to reduce block latency by disabling garbage collection during Engine API calls.", DefaultValue = "true")]
    public bool PrioritizeBlockLatency { get; set; }

    [ConfigItem(Description = "The garbage collection (GC) mode between Engine API calls.", DefaultValue = nameof(GcLevel.Gen1))]
    public GcLevel SweepMemory { get; set; }

    [ConfigItem(Description = $"The compaction mode for ordinary post-block collections; periodic decommit collections always fully compact. No requests non-blocking collection, which may be skipped during background GC and may increase steady-state memory usage. When set to `{nameof(GcCompaction.Full)}`, compacts the large object heap (LOH) if `{nameof(SweepMemory)}` is set to `{nameof(GcLevel.Gen2)}`.",
        DefaultValue = nameof(GcCompaction.No))]
    public GcCompaction CompactMemory { get; set; }

    [ConfigItem(Description = """
            The number of eligible newPayload calls between compacting collections that release process memory. Once due, the decommit runs only while the memory load is at or above `DecommitMemoryLoadPercent`, or once `CollectionsPerUnconditionalDecommit` calls have been counted; until then it is checked again after every call. Decommit waits for at least three seconds after payload completion (or PostBlockGcDelayMs, if longer), after the ordinary post-block collection has run; a new payload cancels the wait without clearing the count.

            Allowed values:

            - `-1`: No requests.
            - `0`: Requests every time.
            - A positive number: Requests after that many eligible newPayload calls, including calls whose entry was skipped or pending collection was cancelled. Calls made while the no-GC strategy is disabled (such as during sync) do not count.


            """, DefaultValue = "25")]
    public int CollectionsPerDecommit { get; set; }

    [ConfigItem(Description = """
            The memory load, in percent of physical memory or of the container's memory limit, at or above which a due decommit (see `CollectionsPerDecommit`) runs. The load is the one the runtime recorded at its last collection. Below it, the decommit waits for `CollectionsPerUnconditionalDecommit`. In a container with a memory limit, the load includes the container's active page cache (the runtime subtracts only inactive file pages), so a container with a warm cache, such as recently read database files, can stay at or above the threshold and then decommits whenever due, as before.

            Allowed values:

            - `0`: Decommits whenever due, whatever the memory load.
            - A number from 1 to 100: Decommits when due and the memory load is at least that percentage.


            """, DefaultValue = "70")]
    public int DecommitMemoryLoadPercent { get; set; }

    [ConfigItem(Description = """
            The number of eligible newPayload calls after which a due decommit (see `CollectionsPerDecommit`) runs even below `DecommitMemoryLoadPercent`.

            Allowed values:

            - `-1`: Decommits only at or above `DecommitMemoryLoadPercent`.
            - A positive number: Decommits after that many eligible newPayload calls since the last decommit. A number below `CollectionsPerDecommit` decommits whenever due.


            """, DefaultValue = "300")]
    public int CollectionsPerUnconditionalDecommit { get; set; }

    [ConfigItem(Description = "The timeout, in milliseconds, for the `engine_newPayload` method.", DefaultValue = "7000", HiddenFromDocs = true)]
    public int NewPayloadBlockProcessingTimeout { get; set; }

    [ConfigItem(Description = "Cache NewPayload valid or invalid results", DefaultValue = "50", HiddenFromDocs = true)]
    public int NewPayloadCacheSize { get; }

    [ConfigItem(Description = "[TECHNICAL] Simulate block production for every possible slot. Just for stress-testing purposes.", DefaultValue = "false", HiddenFromDocs = true)]
    bool SimulateBlockProduction { get; set; }

    [ConfigItem(Description = "Delay, in milliseconds, between `newPayload` and GC trigger. If not set, defaults to 1/8th of `Blocks.SecondsPerSlot`.", DefaultValue = null, HiddenFromDocs = true)]
    int? PostBlockGcDelayMs { get; set; }
}
