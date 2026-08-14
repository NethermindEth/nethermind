// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Specs;

public abstract class ForkScheduleSpecProvider : IForkAwareSpecProvider
{
    public const ulong GenesisBlockNumber = 0;
    public const ulong GenesisTimestamp = 0UL;

    private readonly Lazy<ForkSpec[]> _schedule;
    private readonly Lazy<FrozenDictionary<string, IReleaseSpec>> _forks;
    private readonly Lazy<string[]> _availableForks;
    private readonly Lazy<Index> _index;

    protected internal ForkSpec[] ForkSchedule => _schedule.Value;
    public FrozenDictionary<string, IReleaseSpec> Forks => _forks.Value;
    public IEnumerable<string> AvailableForks => _availableForks.Value;
    public bool TryGetForkSpec(string forkName, out IReleaseSpec? spec) => Forks.TryGetValue(forkName, out spec);

    protected ForkScheduleSpecProvider(ForkSpec[] schedule, UInt256? terminalTotalDifficulty = null, ForkActivation? mergeBlockNumber = null)
        : this(new Lazy<ForkSpec[]>(schedule), terminalTotalDifficulty, mergeBlockNumber) { }

    protected ForkScheduleSpecProvider(Func<ForkSpec[]> scheduleFactory, UInt256? terminalTotalDifficulty = null, ForkActivation? mergeBlockNumber = null)
        : this(new Lazy<ForkSpec[]>(scheduleFactory), terminalTotalDifficulty, mergeBlockNumber) { }

    private ForkScheduleSpecProvider(Lazy<ForkSpec[]> schedule, UInt256? terminalTotalDifficulty, ForkActivation? mergeBlockNumber)
    {
        _schedule = schedule;
        _forks = new(() => ForkSchedule.ToFrozenDictionary(static x => x.Spec.Name, static x => x.Spec, StringComparer.OrdinalIgnoreCase));
        _availableForks = new(() => [.. ForkSchedule.Select(static x => x.Spec.Name)]);
        _index = new(() => Index.Build(ForkSchedule));
        TerminalTotalDifficulty = terminalTotalDifficulty;
        MergeBlockNumber = mergeBlockNumber;
    }

    public IReleaseSpec GetSpec(ForkActivation forkActivation)
    {
        Index idx = _index.Value;
        // Timestamp-keyed forks apply only when the block is at or past the last block-keyed
        // fork: a pre-merge block carries a pre-merge spec regardless of what its real-world
        // timestamp would land in.
        return forkActivation.Timestamp is ulong ts
            && forkActivation.BlockNumber >= idx.LastBlockKey
            && idx.LookupTimestamp(ts) is { } tsSpec
            ? tsSpec
            : idx.LookupBlock(forkActivation.BlockNumber);
    }

    /// <remarks>
    /// Parallel sorted arrays keyed by block number and timestamp let <see cref="GetSpec"/>
    /// do two <c>O(log n)</c> binary searches instead of scanning the whole schedule on
    /// every consensus-path call.
    /// </remarks>
    private readonly struct Index
    {
        private readonly ulong[] _blockKeys;
        private readonly IReleaseSpec[] _blockSpecs;
        private readonly ulong[] _timestampKeys;
        private readonly IReleaseSpec[] _timestampSpecs;

        private Index(ulong[] blockKeys, IReleaseSpec[] blockSpecs, ulong[] timestampKeys, IReleaseSpec[] timestampSpecs)
        {
            _blockKeys = blockKeys;
            _blockSpecs = blockSpecs;
            _timestampKeys = timestampKeys;
            _timestampSpecs = timestampSpecs;
        }

        public ulong LastBlockKey => _blockKeys[^1];

        public static Index Build(ForkSpec[] schedule)
        {
            int blockCount = 0;
            int timestampCount = 0;
            foreach (ForkSpec fork in schedule)
            {
                if (fork.Block.HasValue) blockCount++;
                else if (fork.Timestamp.HasValue) timestampCount++;
            }

            ulong[] blockKeys = new ulong[blockCount];
            IReleaseSpec[] blockSpecs = new IReleaseSpec[blockCount];
            ulong[] timestampKeys = new ulong[timestampCount];
            IReleaseSpec[] timestampSpecs = new IReleaseSpec[timestampCount];

            int bi = 0;
            int ti = 0;
            foreach (ForkSpec fork in schedule)
            {
                if (fork.Block is ulong b) { blockKeys[bi] = b; blockSpecs[bi] = fork.Spec; bi++; }
                else if (fork.Timestamp is ulong t) { timestampKeys[ti] = t; timestampSpecs[ti] = fork.Spec; ti++; }
            }

            return new Index(blockKeys, blockSpecs, timestampKeys, timestampSpecs);
        }

        // Schedule always has a genesis (block 0) entry, so idx >= 0 for any non-negative block number.
        public IReleaseSpec LookupBlock(ulong blockNumber) =>
            _blockSpecs[FindLastAtMost(_blockKeys, blockNumber)];

        public IReleaseSpec? LookupTimestamp(ulong timestamp)
        {
            if (_timestampKeys.Length == 0) return null;
            int idx = FindLastAtMost(_timestampKeys, timestamp);
            return idx < 0 ? null : _timestampSpecs[idx];
        }

        // Largest index i such that keys[i] <= value, or -1 if all keys are larger.
        // When duplicates of value exist, returns the rightmost matching index — so the
        // last-declared activation wins (matters for Hoodi where Shanghai and Cancun
        // share timestamp 0).
        private static int FindLastAtMost<T>(ReadOnlySpan<T> sortedKeys, T value) where T : IComparable<T>
        {
            int idx = sortedKeys.BinarySearch(value);
            if (idx < 0) return ~idx - 1;
            while (idx + 1 < sortedKeys.Length && sortedKeys[idx + 1].CompareTo(value) == 0) idx++;
            return idx;
        }
    }

    public void UpdateMergeTransitionInfo(ulong? blockNumber, UInt256? terminalTotalDifficulty = null)
    {
        if (blockNumber is not null)
            MergeBlockNumber = (ForkActivation)blockNumber;
        if (terminalTotalDifficulty is not null)
            TerminalTotalDifficulty = terminalTotalDifficulty;
    }

    public ForkActivation? MergeBlockNumber { get; private set; }
    public UInt256? TerminalTotalDifficulty { get; private set; }
    public IReleaseSpec GenesisSpec => ForkSchedule[0].Spec;
    public virtual ulong? DaoBlockNumber => null;
    public virtual ulong ChainId => NetworkId;
    public ForkActivation[] TransitionActivations { get; protected set; } = [];

    public abstract ulong TimestampFork { get; }
    public abstract ulong NetworkId { get; }
    public abstract ulong? BeaconChainGenesisTimestamp { get; }
}
