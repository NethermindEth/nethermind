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
    public const int DefaultIntervalLog2 = 9;
    public const int MinIntervalLog2 = 6;
    public const int MaxIntervalLog2 = 12;
    public const int FullVectorEvery = 256;
    public const int StampLength = 5;

    public static readonly CommitmentDepthPolicy Default = new(DefaultIntervalLog2);

    public static CommitmentDepthPolicy FromConfig(IFlatDbConfig config) =>
        config.ArchiveProofCheckpointIntervalLog2 <= 0 ? Default : new CommitmentDepthPolicy(config.ArchiveProofCheckpointIntervalLog2);

    public CommitmentDepthPolicy(int intervalLog2)
        : this(intervalLog2, DefaultAccountExactDepth, DefaultAccountCheckpointDepth, DefaultStorageExactDepth, DefaultStorageCheckpointDepth, DefaultLargeTrieSignalDepth, DefaultStorageRowsSignalDepth)
    {
    }

    public CommitmentDepthPolicy(int intervalLog2, int accountExactDepth, int accountCheckpointDepth, int storageExactDepth, int storageCheckpointDepth, int largeTrieSignalDepth, int storageRowsSignalDepth)
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

        IntervalLog2 = intervalLog2;
        AccountExactDepth = accountExactDepth;
        AccountCheckpointDepth = accountCheckpointDepth;
        StorageExactDepth = storageExactDepth;
        StorageCheckpointDepth = storageCheckpointDepth;
        LargeTrieSignalDepth = largeTrieSignalDepth;
        StorageRowsSignalDepth = storageRowsSignalDepth;
    }

    public int IntervalLog2 { get; }

    public ulong Interval => 1UL << IntervalLog2;

    public int AccountExactDepth { get; }

    public int AccountCheckpointDepth { get; }

    public int StorageExactDepth { get; }

    public int StorageCheckpointDepth { get; }

    public int LargeTrieSignalDepth { get; }

    public int StorageRowsSignalDepth { get; }

    public bool IsExactAccountDepth(int depth) => depth <= AccountExactDepth;

    internal CommitmentTier AccountTier(int depth) =>
        depth switch
        {
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

    public static bool IsFullVectorSuffix(ulong suffix) => suffix % FullVectorEvery == 0;

    public void WriteStamp(Span<byte> destination)
    {
        destination[0] = (byte)IntervalLog2;
        destination[1] = (byte)((AccountExactDepth << 4) | AccountCheckpointDepth);
        destination[2] = (byte)((StorageExactDepth << 4) | StorageCheckpointDepth);
        destination[3] = (byte)LargeTrieSignalDepth;
        destination[4] = (byte)StorageRowsSignalDepth;
    }

    public bool MatchesStamp(ReadOnlySpan<byte> stamp)
    {
        if (stamp.Length != StampLength) return false;

        Span<byte> own = stackalloc byte[StampLength];
        WriteStamp(own);
        return stamp.SequenceEqual(own);
    }

    public override string ToString() =>
        $"K=2^{IntervalLog2}, accounts exact<={AccountExactDepth} checkpoint<={AccountCheckpointDepth}, storage rows once a trie has reached depth {StorageRowsSignalDepth}: exact<={StorageExactDepth} (once depth {LargeTrieSignalDepth}) checkpoint<={StorageCheckpointDepth}";
}
