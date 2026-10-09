// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.State.Pbt.Image;

/// <summary>One ordered offline account preimage source, with a streaming slot sequence.</summary>
public readonly record struct PbtAccountPreimages(Address Address, uint SlotCount, IEnumerable<ValueHash256> Slots);

/// <summary>Reads EIP-8347 preimages with constant memory, including accounts with arbitrarily many slots.</summary>
/// <remarks>The caller owns the stream and must read all declared slots before advancing accounts.
/// Reading accounts until false validates EOF. Slot values are full big-endian raw keys, not their hashes.</remarks>
public sealed class PbtPreimageReader(Stream source)
{
    private ValueHash256? _previousAccountHash;
    private ValueHash256? _previousSlotHash;
    private uint _remainingSlots;

    /// <summary>The Keccak path of the account read last, which ascends through the stream.</summary>
    public ValueHash256? AccountHash => _previousAccountHash;

    /// <summary>The Keccak path of the slot read last, which ascends through its account's slots.</summary>
    public ValueHash256? SlotHash => _previousSlotHash;

    public bool ReadAccount([NotNullWhen(true)] out Address? address, out uint slotCount, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_remainingSlots != 0) throw new InvalidOperationException("Read all account slots before advancing.");
        int first = source.ReadByte();
        address = null;
        slotCount = 0;
        if (first == -1) return false;
        Span<byte> record = stackalloc byte[24];
        record[0] = (byte)first;
        PbtSnapshotCodec.ReadRecord(source, record[1..]);
        ValueHash256 hash = ValueKeccak.Compute(record[..20]);
        CheckOrder(hash, _previousAccountHash);
        _previousAccountHash = hash;
        _previousSlotHash = null;
        address = new Address(record[..20]);
        slotCount = _remainingSlots = BinaryPrimitives.ReadUInt32BigEndian(record[20..]);
        return true;
    }

    public ValueHash256 ReadSlot(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_remainingSlots == 0) throw new InvalidOperationException("No remaining account slots.");
        Span<byte> slot = stackalloc byte[32];
        PbtSnapshotCodec.ReadRecord(source, slot);
        ValueHash256 hash = ValueKeccak.Compute(slot);
        CheckOrder(hash, _previousSlotHash);
        _previousSlotHash = hash;
        _remainingSlots--;
        return new ValueHash256(slot);
    }

    private static void CheckOrder(in ValueHash256 hash, ValueHash256? previous)
    {
        if (previous is { } previousHash && previousHash.Bytes.SequenceCompareTo(hash.Bytes) >= 0)
            throw new InvalidDataException("Preimages must be strictly ordered by Keccak path.");
    }
}

/// <summary>Writes already Keccak-path-ordered preimages without sorting or retaining account slot lists.</summary>
/// <remarks>The caller owns the output. A failed/cancelled write leaves a partial, unpublished artifact.</remarks>
public static class PbtPreimageCodec
{
    public static void Write(Stream destination, IEnumerable<PbtAccountPreimages> accounts, CancellationToken cancellationToken = default)
    {
        Span<byte> header = stackalloc byte[24];
        foreach (PbtAccountPreimages account in accounts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            account.Address.Bytes.CopyTo(header);
            BinaryPrimitives.WriteUInt32BigEndian(header[20..], account.SlotCount);
            destination.Write(header);
            foreach (ValueHash256 slot in account.Slots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                destination.Write(slot.Bytes);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
    }
}
