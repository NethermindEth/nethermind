// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Exceptions;
using Nethermind.Db;

namespace Nethermind.State.Flat.History.Proofs;

public sealed class CommitmentDepthPolicy
{
    public const int MaxTrieDepth = 64;
    public const int MaxStampedDepth = 15;
    public const int DefaultAccountExactDepth = 2;
    public const int DefaultAccountCheckpointDepth = 5;
    public const int DefaultStorageExactDepth = 2;
    public const int DefaultStorageCheckpointDepth = 4;
    public const int DefaultLargeTrieSignalDepth = 6;
    public const int DefaultStorageRowsSignalDepth = 4;
    public const int DefaultAccountComposedDepths = 1 << 1;
    public const int DefaultEpochLog2 = 19;
    public const int MaxEpochLog2 = 30;
    public const int DefaultIntervalLog2 = 9;
    public const int MinIntervalLog2 = 6;
    public const int MaxIntervalLog2 = 12;
    public const int FullVectorEvery = 256;
    public const int StampLength = 7;

    public static readonly CommitmentDepthPolicy Default = new(DefaultIntervalLog2);

    public static CommitmentDepthPolicy FromConfig(IFlatDbConfig config)
    {
        int intervalLog2 = config.ArchiveProofCheckpointIntervalLog2 <= 0 ? DefaultIntervalLog2 : config.ArchiveProofCheckpointIntervalLog2;
        int epochLog2 = config.ArchiveProofEpochLog2 <= 0 ? DefaultEpochLog2 : config.ArchiveProofEpochLog2;
        return intervalLog2 == DefaultIntervalLog2 && epochLog2 == DefaultEpochLog2
            ? Default
            : new CommitmentDepthPolicy(intervalLog2, DefaultAccountExactDepth, DefaultAccountCheckpointDepth, DefaultStorageExactDepth, DefaultStorageCheckpointDepth, DefaultLargeTrieSignalDepth, DefaultStorageRowsSignalDepth, DefaultAccountComposedDepths, epochLog2);
    }

    public CommitmentDepthPolicy(int intervalLog2)
        : this(intervalLog2, DefaultAccountExactDepth, DefaultAccountCheckpointDepth, DefaultStorageExactDepth, DefaultStorageCheckpointDepth, DefaultLargeTrieSignalDepth, DefaultStorageRowsSignalDepth)
    {
    }

    public CommitmentDepthPolicy(int intervalLog2, int accountExactDepth, int accountCheckpointDepth, int storageExactDepth, int storageCheckpointDepth, int largeTrieSignalDepth, int storageRowsSignalDepth, int accountComposedDepths = DefaultAccountComposedDepths, int epochLog2 = DefaultEpochLog2)
    {
        if (intervalLog2 is < MinIntervalLog2 or > MaxIntervalLog2)
        {
            throw new InvalidConfigurationException(
                $"FlatDb.ArchiveProofCheckpointIntervalLog2={intervalLog2} is outside the supported {MinIntervalLog2}..{MaxIntervalLog2}. " +
                "Below it the commitment columns grow past the archive for no latency gain; above it every proof replays a window " +
                "of changes long enough to take seconds.", -1);
        }

        if (accountExactDepth < 0 || accountExactDepth > accountCheckpointDepth || accountCheckpointDepth > MaxStampedDepth)
        {
            throw new InvalidConfigurationException($"Account commitment depths exact<={accountExactDepth}, checkpoint<={accountCheckpointDepth} are not ordered or exceed the stamped maximum {MaxStampedDepth}.", -1);
        }

        if (storageExactDepth < 0 || storageExactDepth > storageCheckpointDepth || storageCheckpointDepth > MaxStampedDepth || largeTrieSignalDepth <= storageExactDepth || largeTrieSignalDepth > MaxTrieDepth)
        {
            throw new InvalidConfigurationException($"Storage commitment depths exact<={storageExactDepth}, checkpoint<={storageCheckpointDepth}, large-trie signal {largeTrieSignalDepth} are not ordered or exceed the stamped maximum {MaxStampedDepth}.", -1);
        }

        if (storageRowsSignalDepth < 1 || storageRowsSignalDepth > largeTrieSignalDepth)
        {
            throw new InvalidConfigurationException($"The storage rows signal depth {storageRowsSignalDepth} must lie in 1..{largeTrieSignalDepth} (the large-trie signal).", -1);
        }

        if (accountComposedDepths < 0 || accountComposedDepths > byte.MaxValue || (accountComposedDepths & 1) != 0 || accountComposedDepths >> accountExactDepth != 0)
        {
            throw new InvalidConfigurationException($"Composed account depths {accountComposedDepths:b} must lie strictly between the root and the exact depth {accountExactDepth}.", -1);
        }

        if (epochLog2 < intervalLog2 || epochLog2 > MaxEpochLog2)
        {
            throw new InvalidConfigurationException($"The commitment epoch 2^{epochLog2} must span at least one checkpoint window (2^{intervalLog2}) and at most 2^{MaxEpochLog2} blocks.", -1);
        }

        IntervalLog2 = intervalLog2;
        AccountExactDepth = accountExactDepth;
        AccountCheckpointDepth = accountCheckpointDepth;
        StorageExactDepth = storageExactDepth;
        StorageCheckpointDepth = storageCheckpointDepth;
        LargeTrieSignalDepth = largeTrieSignalDepth;
        StorageRowsSignalDepth = storageRowsSignalDepth;
        AccountComposedDepths = accountComposedDepths;
        EpochLog2 = epochLog2;
    }

    public int IntervalLog2 { get; }

    public ulong Interval => 1UL << IntervalLog2;

    public int AccountExactDepth { get; }

    public int AccountCheckpointDepth { get; }

    public int StorageExactDepth { get; }

    public int StorageCheckpointDepth { get; }

    public int LargeTrieSignalDepth { get; }

    public int StorageRowsSignalDepth { get; }

    public int AccountComposedDepths { get; }

    public int EpochLog2 { get; }

    public ulong EpochBlocks => 1UL << EpochLog2;

    public ulong Epoch(ulong block) => block >> EpochLog2;

    public ulong EpochOfWindow(ulong window) => window >> (EpochLog2 - IntervalLog2);

    public ulong EpochStart(ulong epoch) => epoch << EpochLog2;

    public bool IsEpochStartWindow(ulong window) => (window & ((1UL << (EpochLog2 - IntervalLog2)) - 1)) == 0;

    public bool IsComposedAccountDepth(int depth) => depth < 8 && ((AccountComposedDepths >> depth) & 1) != 0;

    public bool IsExactAccountDepth(int depth) => depth <= AccountExactDepth && !IsComposedAccountDepth(depth);

    internal CommitmentTier AccountTier(int depth) =>
        depth switch
        {
            _ when IsComposedAccountDepth(depth) => CommitmentTier.Composed,
            _ when IsExactAccountDepth(depth) => CommitmentTier.PerChange,
            _ when depth <= AccountCheckpointDepth => CommitmentTier.Checkpoint,
            _ => CommitmentTier.Recomputed,
        };

    internal CommitmentTier StorageTier(int depth, int trieDepthReached) =>
        depth switch
        {
            _ when trieDepthReached >= LargeTrieSignalDepth && depth <= StorageExactDepth => CommitmentTier.PerChange,
            _ when trieDepthReached >= StorageRowsSignalDepth && depth <= StorageCheckpointDepth => CommitmentTier.Checkpoint,
            _ => CommitmentTier.Recomputed,
        };

    public bool StorageTrieHasRows(int trieDepthReached) => trieDepthReached >= StorageRowsSignalDepth;

    public bool StorageTrieHasExactRows(int trieDepthReached) => trieDepthReached >= LargeTrieSignalDepth;

    public ulong WindowAtOrBelow(ulong block) => block >> IntervalLog2;

    public ulong WindowClosingAt(ulong block) => (block + Interval - 1) >> IntervalLog2;

    public bool ClosesWindow(ulong block) => (block & (Interval - 1)) == 0;

    public bool IsFullVectorSuffix(ulong suffix) => suffix % FullVectorEvery == 0 || IsEpochStartWindow(suffix);

    public void WriteStamp(Span<byte> destination)
    {
        destination[0] = (byte)IntervalLog2;
        destination[1] = (byte)((AccountExactDepth << 4) | AccountCheckpointDepth);
        destination[2] = (byte)((StorageExactDepth << 4) | StorageCheckpointDepth);
        destination[3] = (byte)LargeTrieSignalDepth;
        destination[4] = (byte)StorageRowsSignalDepth;
        destination[5] = (byte)AccountComposedDepths;
        destination[6] = (byte)EpochLog2;
    }

    public bool MatchesStamp(ReadOnlySpan<byte> stamp)
    {
        if (stamp.Length != StampLength) return false;

        Span<byte> own = stackalloc byte[StampLength];
        WriteStamp(own);
        return stamp.SequenceEqual(own);
    }

    public override string ToString() =>
        $"K=2^{IntervalLog2}, epoch 2^{EpochLog2}, accounts exact<={AccountExactDepth} (composed at {AccountComposedDepths:b}) checkpoint<={AccountCheckpointDepth}, storage rows once a trie has reached depth {StorageRowsSignalDepth}: exact<={StorageExactDepth} (once depth {LargeTrieSignalDepth}) checkpoint<={StorageCheckpointDepth}";
}
