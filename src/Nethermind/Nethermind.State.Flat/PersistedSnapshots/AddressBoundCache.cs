// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Nethermind.Core;
using Nethermind.Core.Utils;
using Nethermind.State.Flat.Hsst;
using Nethermind.State.Flat.PersistedSnapshots.Storage;

namespace Nethermind.State.Flat.PersistedSnapshots;

/// <summary>
/// Single 8-way set-associative clock (second-chance) address-bound cache, mirroring
/// <see cref="PageResidencyTracker"/>'s hot/miss-path split. One set ⇒ 8 ways × 8 bytes
/// = 64 bytes stored inline as a <see cref="Vector512{T}"/> field — no separate heap
/// allocation. <see cref="Vector512{T}"/> provides natural 64-byte alignment, keeping the
/// cache in a single cache line. It is never used as a SIMD vector — purely an
/// alignment-bearing storage cell, reinterpreted as <c>Span&lt;long&gt;</c> via
/// <see cref="MemoryMarshal.CreateSpan{T}(ref T,int)"/>.
/// </summary>
/// <remarks>
/// Each slot packs:
/// <list type="bullet">
///   <item>bit 63: REF — armed on every hit and insert, cleared by the clock hand on a miss-pass.</item>
///   <item>bit 62: VALID — distinguishes an empty (0L) slot from a stored (tag=0, offset=0) entry.</item>
///   <item>bits 46..61: 16-bit tag (bytes 4..6 of the raw Address).</item>
///   <item>bits 0..45: 46-bit absolute offset of the entry's FlagByte in the outer column 0x01
///         entry. 46 bits = 64 TiB, ample for any real snapshot.</item>
/// </list>
/// keyFirst=false BTree entry shape is [Value][FlagByte][LEB128][FullKey]; on a tag match the
/// FlagByte, LEB128 (≤ 6 bytes) and 20-byte stored raw Address are read and compared to the
/// lookup Address to catch tag collisions / layout drift. The cached Bound is
/// (flagByteOffset - valueLength, valueLength). Must be accessed only as an in-place field —
/// the lock-free scans and the per-cache spin-lock operate on the storage by ref.
/// </remarks>
internal struct AddressBoundCache
{
    private const long RefBit = unchecked((long)0x8000_0000_0000_0000UL);
    private const long ValidBit = 0x4000_0000_0000_0000L;
    private const long KeyMask = ~RefBit;
    private const long OffsetMask = (1L << 46) - 1;
    private const int TagShift = 46;
    private const int Ways = 8;
    private const int WayMask = Ways - 1;
    private const int MetaLockBit = 1 << 7;
    private const int MetaHandMask = 0x7;
    // FlagByte (1) + LEB128 value-length (≤ 6) + raw Address (20).
    private const int ProbeBytes = 1 + 6 + PersistedSnapshotTags.AddressKeyLength;

    private Vector512<long> _slots;
    private int _meta;

    /// <summary>
    /// Hot-path lookup: lock-free 8-way scan. A tag match is a candidate, verified against the
    /// 20-byte stored raw Address on disk via <paramref name="reader"/> to filter the
    /// inevitable collisions; the matching slot's REF bit is re-armed before returning.
    /// </summary>
    public bool TryGet(in ArenaByteReader reader, Address address, out Bound bound)
    {
        Span<long> slots = MemoryMarshal.CreateSpan(
            ref Unsafe.As<Vector512<long>, long>(ref _slots), Ways);
        ushort hashTag = MemoryMarshal.Read<ushort>(address.Bytes.Slice(4, 2));
        for (int w = 0; w < Ways; w++)
        {
            long s = Volatile.Read(ref slots[w]);
            if ((s & ValidBit) == 0) continue;
            if ((ushort)((s >>> TagShift) & 0xFFFF) != hashTag) continue;

            long flagOffset = s & OffsetMask;
            Span<byte> probe = stackalloc byte[ProbeBytes];
            if (!reader.TryRead(flagOffset, probe)) continue;
            // probe[0] is the entry's FlagByte; the LEB128 value-length starts at probe[1].
            int pos = 1;
            long valueLength = Leb128.Read(probe, ref pos);
            if (!probe.Slice(pos, PersistedSnapshotTags.AddressKeyLength)
                    .SequenceEqual(address.Bytes))
                continue;

            if ((s & RefBit) == 0)
                Interlocked.Or(ref slots[w], RefBit);
            bound = new Bound(flagOffset - valueLength, valueLength);
            return true;
        }
        bound = default;
        return false;
    }

    /// <summary>
    /// Miss-path insert of the entry whose FlagByte sits at <paramref name="flagByteOffset"/>.
    /// Takes the per-cache spin-lock, then re-scans for an existing matching entry, an empty
    /// way, and finally the clock victim.
    /// </summary>
    public void Insert(Address address, long flagByteOffset)
    {
        ushort hashTag = MemoryMarshal.Read<ushort>(address.Bytes.Slice(4, 2));
        long newEntry = ValidBit
                      | RefBit
                      | ((long)hashTag << TagShift)
                      | (flagByteOffset & OffsetMask);

        ref int meta = ref _meta;
        AcquireLock(ref meta);
        try
        {
            Span<long> slots = MemoryMarshal.CreateSpan(
                ref Unsafe.As<Vector512<long>, long>(ref _slots), Ways);
            // Re-scan under the lock — another miss-path racer may already have installed
            // this exact (tag, offset) pair, in which case just re-arm its REF bit.
            for (int w = 0; w < Ways; w++)
            {
                long s = slots[w];
                if ((s & KeyMask) == (newEntry & KeyMask))
                {
                    Volatile.Write(ref slots[w], s | RefBit);
                    return;
                }
            }

            // Look for an empty way (VALID=0). New arrivals already carry REF=1 so they
            // survive the first clock pass.
            for (int w = 0; w < Ways; w++)
            {
                if (slots[w] == 0L)
                {
                    Volatile.Write(ref slots[w], newEntry);
                    return;
                }
            }

            // Set is full — run the clock. Worst case: 8 set-REFs ⇒ one full pass clears
            // them, the second pass finds an unreferenced way. Bound at 2*Ways iterations.
            int hand = meta & MetaHandMask;
            for (int i = 0; i < 2 * Ways; i++)
            {
                long s = slots[hand];
                if ((s & RefBit) != 0)
                {
                    Volatile.Write(ref slots[hand], s & ~RefBit);
                    hand = (hand + 1) & WayMask;
                    continue;
                }

                Volatile.Write(ref slots[hand], newEntry);
                hand = (hand + 1) & WayMask;
                meta = (meta & ~MetaHandMask) | hand;
                return;
            }

            Debug.Fail("Clock scan failed to find a victim");
        }
        finally
        {
            ReleaseLock(ref meta);
        }
    }

    // A hand-rolled spin-lock rather than System.Threading.SpinLock: the lock bit
    // (MetaLockBit) is packed into _meta alongside the clock hand (MetaHandMask), keeping
    // the cache's whole mutable state in one int so the struct stays inline on the snapshot.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AcquireLock(ref int meta)
    {
        SpinWait spinner = default;
        while (true)
        {
            int observed = Volatile.Read(ref meta);
            if ((observed & MetaLockBit) == 0)
            {
                int withLock = observed | MetaLockBit;
                if (Interlocked.CompareExchange(ref meta, withLock, observed) == observed)
                    return;
            }
            spinner.SpinOnce();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ReleaseLock(ref int meta) =>
        Volatile.Write(ref meta, meta & ~MetaLockBit);
}
