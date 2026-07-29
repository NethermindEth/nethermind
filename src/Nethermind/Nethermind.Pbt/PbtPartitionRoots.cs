// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using NodeKind = Nethermind.Pbt.PbtTrieNodeGroup.NodeKind;

namespace Nethermind.Pbt;

/// <summary>The kind and node hash at one partition's fixed-prefix root.</summary>
public readonly record struct PbtPartitionRoot(NodeKind Kind, ValueHash256 Hash)
{
    /// <summary>Validates a root read from durable metadata.</summary>
    /// <exception cref="InvalidDataException">The kind or hash cannot describe a partition root.</exception>
    internal void Validate()
    {
        if (Kind is not (NodeKind.Absent or NodeKind.Internal or NodeKind.Stem))
            throw new InvalidDataException($"A partition root cannot have kind {Kind}");
        if ((Kind == NodeKind.Absent) != (Hash == default))
            throw new InvalidDataException("A partition root is absent exactly when its node hash is zero");
    }
}

/// <summary>The three partition roots carried with a state, and the full EIP-8297 root they derive.</summary>
public sealed class PbtPartitionRoots
{
    private const int RootEncodedLength = sizeof(byte) + ValueHash256.MemorySize;

    public const int EncodedLength = PbtPartitions.Count * RootEncodedLength;

    private readonly PbtPartitionRoot[] _roots;

    private PbtPartitionRoots(PbtPartitionRoot[] roots)
    {
        _roots = roots;
        Root = DeriveRoot(roots);
    }

    public static PbtPartitionRoots Empty { get; } = new(new PbtPartitionRoot[PbtPartitions.Count]);

    public PbtPartitionRoot this[PbtPartition partition] => _roots[(int)partition];

    /// <summary>The EIP-8297 root obtained by folding the fixed partition prefixes.</summary>
    public ValueHash256 Root { get; }

    /// <summary>Returns a copy with <paramref name="partition"/> replaced.</summary>
    public PbtPartitionRoots With(PbtPartition partition, in PbtPartitionRoot root)
    {
        root.Validate();
        PbtPartitionRoot[] roots = (PbtPartitionRoot[])_roots.Clone();
        roots[(int)partition] = root;
        return new PbtPartitionRoots(roots);
    }

    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length != EncodedLength) throw new ArgumentException($"Partition roots require {EncodedLength} bytes", nameof(destination));

        int offset = 0;
        foreach (PbtPartition partition in PbtPartitions.All)
        {
            PbtPartitionRoot root = this[partition];
            destination[offset] = (byte)root.Kind;
            root.Hash.Bytes.CopyTo(destination[(offset + sizeof(byte))..]);
            offset += RootEncodedLength;
        }
    }

    /// <exception cref="InvalidDataException"><paramref name="data"/> is not an encoded set of partition roots.</exception>
    public static PbtPartitionRoots Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length != EncodedLength) throw new InvalidDataException($"Partition roots length {data.Length} is not {EncodedLength}");

        PbtPartitionRoot[] roots = new PbtPartitionRoot[PbtPartitions.Count];
        for (int i = 0, offset = 0; i < roots.Length; i++, offset += RootEncodedLength)
        {
            PbtPartitionRoot root = new((NodeKind)data[offset], new ValueHash256(data.Slice(offset + sizeof(byte), ValueHash256.MemorySize)));
            root.Validate();
            roots[i] = root;
        }

        return new PbtPartitionRoots(roots);
    }

    private static ValueHash256 DeriveRoot(PbtPartitionRoot[] roots)
    {
        PbtPartitionRoot accountAndCode = FoldPair(roots[(int)PbtPartition.Account], roots[(int)PbtPartition.Code]);
        PbtPartitionRoot nonStorage = FoldWithAbsentRight(FoldWithAbsentRight(accountAndCode));
        return FoldPair(nonStorage, roots[(int)PbtPartition.Storage]).Hash;
    }

    private static PbtPartitionRoot FoldWithAbsentRight(in PbtPartitionRoot node) =>
        FoldPair(node, default);

    private static PbtPartitionRoot FoldPair(in PbtPartitionRoot left, in PbtPartitionRoot right)
    {
        if (left.Kind == NodeKind.Absent)
        {
            return right.Kind switch
            {
                NodeKind.Absent => default,
                NodeKind.Stem => right,
                _ => new PbtPartitionRoot(NodeKind.Internal, Blake3Hash.HashPairOrZero(default, right.Hash)),
            };
        }

        if (right.Kind == NodeKind.Absent)
        {
            return left.Kind == NodeKind.Stem
                ? left
                : new PbtPartitionRoot(NodeKind.Internal, Blake3Hash.HashPairOrZero(left.Hash, default));
        }

        return new PbtPartitionRoot(NodeKind.Internal, Blake3Hash.HashPairOrZero(left.Hash, right.Hash));
    }
}
