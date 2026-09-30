// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt.Image;

/// <summary>The counts an EIP-8347 snapshot writes ahead of its sections and storage records, gathered from its ascending leaves.</summary>
/// <remarks>The writer streams its leaves once, so the counts must come from an earlier pass over the same leaves.
/// Holds one count per storage record.</remarks>
internal sealed class PbtSnapshotLayout
{
    private readonly byte[] _stem = new byte[PbtStorageTreeKey.MaxLength - 1];
    private int _stemLength;
    private ValueHash256? _storageAddress;

    public ulong Headers { get; private set; }
    public ulong CodeGroups { get; private set; }

    /// <summary>The group count of each storage record, in record order.</summary>
    public List<ulong> StorageGroups { get; } = [];

    public void Add(in RebuildEntry entry)
    {
        ReadOnlySpan<byte> stem = entry.Key.Bytes[..^1];
        if (stem.SequenceEqual(_stem.AsSpan(0, _stemLength))) return;
        stem.CopyTo(_stem);
        _stemLength = stem.Length;
        switch (stem[0])
        {
            case Eip8297KeyDerivation.AccountZone:
                Headers++;
                break;
            case Eip8297KeyDerivation.CodeZone:
                CodeGroups++;
                break;
            default:
                ValueHash256 addressHash = new(stem[1..(1 + ValueHash256.MemorySize)]);
                if (_storageAddress != addressHash)
                {
                    _storageAddress = addressHash;
                    StorageGroups.Add(0);
                }
                StorageGroups[^1]++;
                break;
        }
    }

    public static PbtSnapshotLayout Of(IEnumerable<RebuildEntry> leaves)
    {
        PbtSnapshotLayout layout = new();
        foreach (RebuildEntry leaf in leaves) layout.Add(leaf);
        return layout;
    }
}
