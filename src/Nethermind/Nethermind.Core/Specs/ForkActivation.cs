// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Specs;

public readonly struct ForkActivation : IEquatable<ForkActivation>, IComparable<ForkActivation>
{
    public long BlockNumber { get; }
    public ulong? Timestamp { get; }
    public ulong Activation => Timestamp ?? (ulong)BlockNumber;

    public ForkActivation(long blockNumber, ulong? timestamp = null)
    {
        BlockNumber = blockNumber;
        Timestamp = timestamp;
    }

    /// <summary>
    /// Fork activation for forks past The Merge/Paris
    /// </summary>
    /// <param name="timestamp">Timestamp of the fork or check</param>
    /// <returns>Post merge fork activation</returns>
    /// <remarks>
    /// Post Merge are based only on timestamp and we can ignore block number.
    /// </remarks>
    public static ForkActivation TimestampOnly(ulong timestamp) => new(long.MaxValue, timestamp);

    public void Deconstruct(out long blockNumber, out ulong? timestamp)
    {
        blockNumber = BlockNumber;
        timestamp = Timestamp;
    }

    public static explicit operator ForkActivation(long blockNumber) => new(blockNumber);

    public static implicit operator ForkActivation((long blocknumber, ulong? timestamp) forkActivation)
        => new(forkActivation.blocknumber, forkActivation.timestamp);

    public static implicit operator (long blocknumber, ulong? timestamp)(ForkActivation forkActivation)
        => (forkActivation.BlockNumber, forkActivation.Timestamp);

    public bool Equals(ForkActivation other) => BlockNumber == other.BlockNumber && Timestamp == other.Timestamp;

    public override bool Equals(object? obj) => obj is ForkActivation other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(BlockNumber, Timestamp);

    public override string ToString() => $"{BlockNumber} {Timestamp}";

    public int CompareTo(ForkActivation other) =>
        Timestamp is null || other.Timestamp is null
            ? BlockNumber.CompareTo(other.BlockNumber)
            : Timestamp.Value.CompareTo(other.Timestamp.Value);

    public static bool operator ==(ForkActivation first, ForkActivation second) => first.Equals(second);

    public static bool operator !=(ForkActivation first, ForkActivation second) => !first.Equals(second);

    public static bool operator <(ForkActivation first, ForkActivation second) => first.CompareTo(second) < 0;

    public static bool operator >(ForkActivation first, ForkActivation second) => first.CompareTo(second) > 0;

    public static bool operator <=(ForkActivation first, ForkActivation second) => first.CompareTo(second) <= 0;

    public static bool operator >=(ForkActivation first, ForkActivation second) => first.CompareTo(second) >= 0;
}
