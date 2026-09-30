// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;

namespace Nethermind.State.Pbt.Image;

/// <summary>Tracks how much of an address or hash keyspace concurrent workers have walked.</summary>
/// <remarks>The keyspace is split into ascending partitions on its first two bytes. Each partition is walked by one
/// worker at a time, which publishes the key it has reached.</remarks>
internal sealed class PbtKeyspaceProgress(int partitionCount)
{
    private const int PrefixSpace = 1 << 16;

    /// <summary>The walked amount of a whole keyspace, at a 48-bit prefix that retains sub-partition precision.</summary>
    public const ulong Keyspace = 1UL << 48;

    private readonly long[] _walked = new long[partitionCount];

    public void Publish(int partition, ReadOnlySpan<byte> key) =>
        Volatile.Write(ref _walked[partition], (long)(BinaryPrimitives.ReadUInt64BigEndian(key) >> 16) - Boundary(partition));

    public void Complete(int partition) => Volatile.Write(ref _walked[partition], Boundary(partition + 1) - Boundary(partition));

    /// <summary>The walked amount, out of <see cref="Keyspace"/>.</summary>
    public ulong Walked
    {
        get
        {
            ulong walked = 0;
            for (int partition = 0; partition < _walked.Length; partition++) walked += (ulong)Volatile.Read(ref _walked[partition]);
            return walked;
        }
    }

    private long Boundary(int partition) => (long)partition * PrefixSpace / partitionCount << 32;
}
