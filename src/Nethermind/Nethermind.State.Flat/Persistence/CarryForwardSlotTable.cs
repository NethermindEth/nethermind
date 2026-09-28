// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Utils;
using Nethermind.Int256;

namespace Nethermind.State.Flat.Persistence;

/// <summary>
/// A fixed-size, set-associative table of storage-slot reads in native memory, the slot half of
/// <see cref="CarryForwardCachingPersistence"/>.
/// </summary>
/// <remarks>
/// <para>
/// Reads are lock-free through a per-entry seqlock, the pattern <c>KeccakCache</c> uses: read the header, copy the
/// entry, re-read the header, and treat any change as a miss. Every method suffixed <c>NoLock</c> mutates the table
/// and must be called under the owner's lock, so there is only ever one writer.
/// </para>
/// <para>
/// A full set replaces one of its ways instead of the table being wiped, and a wholesale clear bumps the epoch every
/// entry is stamped with, so neither costs time proportional to the table. The block holds no GC references, so the
/// GC neither scans nor moves it. It is freed once the owner and every reader have released their leases, and the
/// entry pointers are dereferenced only by the owner or a reader holding one, which is what keeps them valid.
/// </para>
/// <para>
/// <c>AssociativeCache</c> and <c>SeqlockCache</c> do not fit: the first needs a reference-type value, one allocation
/// per insert, and the second cannot remove an entry, which every committed write needs. Both hold the key's
/// <see cref="Address"/> as a GC reference.
/// </para>
/// </remarks>
internal sealed unsafe class CarryForwardSlotTable : RefCountingDisposable
{
    public const int Ways = 4;
    public const int MaxCapacity = 1 << 28;

    private const uint LockBit = 1;
    private const uint VersionIncrement = 2;
    private const uint VersionMask = 0x0000_FFFE;
    private const uint TagMask = 0xFFFF_0000;
    private const uint EmptyEpoch = 0;
    private const uint FirstEpoch = 1;

    private readonly void* _allocation;
    private readonly Entry* _entries;
    private readonly nuint _setMask;
    private readonly long _allocatedBytes;
    private uint _epoch = FirstEpoch;
    private WriterState _writer;
    private int _freed;

    /// <param name="capacity">Entries to hold; rounded up to a power of two of at least <see cref="Ways"/>.</param>
    public CarryForwardSlotTable(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, MaxCapacity);

        Capacity = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(capacity, Ways));
        _setMask = (nuint)(Capacity / Ways - 1);

        nuint size = (nuint)Capacity * Entry.Size;
        _allocatedBytes = (long)size + Entry.Size;
        // Zeroed memory is handed out by the OS page by page as it is touched, so the resident size follows the fill.
        // The extra entry's worth of bytes lets the entries start on an Entry.Size boundary.
        _allocation = NativeMemory.AllocZeroed((nuint)_allocatedBytes);
        _entries = (Entry*)(((nuint)_allocation + Entry.Size - 1) & ~(nuint)(Entry.Size - 1));
        GC.AddMemoryPressure(_allocatedBytes);
    }

    ~CarryForwardSlotTable() => Free();

    public int Capacity { get; }

    /// <summary>Entries stamped with the current epoch. Read it under the owner's lock.</summary>
    public int Count => _writer.Count;

    internal bool IsAllocated => Volatile.Read(ref _freed) == 0;

    /// <summary>The epoch entries are stamped with; settable so tests can reach the wrap.</summary>
    internal uint Epoch
    {
        get => Volatile.Read(ref _epoch);
        set => Volatile.Write(ref _epoch, value);
    }

    /// <summary>Takes a lease that keeps the memory alive until <see cref="RefCountingDisposable.Dispose"/>.</summary>
    /// <returns><c>false</c> once the table has been released for good.</returns>
    public bool TryLease() => TryAcquireLease();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Hash(Address address, in UInt256 slot) => (ulong)SpanExtensions.FastHash64ForAddressAndSlot(
        ref MemoryMarshal.GetReference(address.Bytes), ref Unsafe.As<UInt256, byte>(ref Unsafe.AsRef(in slot)));

    /// <summary>Looks up a slot read without taking a lock.</summary>
    /// <param name="hash">The key's <see cref="Hash"/>.</param>
    /// <param name="address">The account.</param>
    /// <param name="slot">The storage slot.</param>
    /// <param name="found">Whether the cached read found the slot.</param>
    /// <param name="value">The cached value; <c>default</c> when <paramref name="found"/> is <c>false</c>.</param>
    /// <returns><c>true</c> on a hit. An entry being written at the same time reads as a miss.</returns>
    [SkipLocalsInit]
    public bool TryGet(ulong hash, Address address, in UInt256 slot, out bool found, out UInt256 value)
    {
        Entry* set = SetOf(hash);
        uint tag = TagOf(hash);
        uint epoch = Volatile.Read(ref _epoch);
        ref byte key = ref MemoryMarshal.GetReference(address.Bytes);

        for (int way = 0; way < Ways; way++)
        {
            Entry* entry = set + way;
            uint header = Volatile.Read(ref entry->Header);
            if ((header & (TagMask | LockBit)) != tag) continue;

            // Speculative copy, trusted only if the header is unchanged afterwards.
            uint entryEpoch = entry->Epoch;
            uint entryFound = entry->Found;
            ulong address0 = entry->Address0;
            ulong address1 = entry->Address1;
            uint address2 = entry->Address2;
            UInt256 entrySlot = entry->Slot;
            UInt256 entryValue = entry->Value;

            // On x86/x64 (TSO) loads are not reordered with other loads, so the JIT drops this; elsewhere the copy
            // must complete before the header is read again.
            if (!Sse.IsSupported) Interlocked.MemoryBarrier();

            if (Volatile.Read(ref entry->Header) != header) break;

            if (entryEpoch == epoch
                && AddressEquals(address0, address1, address2, ref key)
                && entrySlot == slot)
            {
                found = entryFound != 0;
                value = entryValue;
                return true;
            }
        }

        found = false;
        value = default;
        return false;
    }

    /// <summary>Caches a slot read, replacing another entry of its set when the set is full.</summary>
    public AddResult AddNoLock(ulong hash, Address address, in UInt256 slot, bool found, in UInt256 value)
    {
        Entry* set = SetOf(hash);
        uint tag = TagOf(hash);
        uint epoch = _epoch;
        ref byte key = ref MemoryMarshal.GetReference(address.Bytes);

        int free = -1;
        for (int way = 0; way < Ways; way++)
        {
            Entry* entry = set + way;
            if (entry->Epoch != epoch)
            {
                if (free < 0) free = way;
            }
            else if ((entry->Header & TagMask) == tag && KeyEquals(entry, ref key, slot))
            {
                // Readers at one generation all read the same state, so the entry already holds this value.
                return AddResult.AlreadyPresent;
            }
        }

        AddResult result;
        if (free >= 0)
        {
            _writer.Count++;
            result = AddResult.Added;
        }
        else
        {
            free = (int)(_writer.VictimCursor++ % Ways);
            result = AddResult.Replaced;
        }

        Entry* target = set + free;
        uint unlocked = BeginWrite(target, tag);
        target->Epoch = epoch;
        target->Found = found ? 1u : 0u;
        target->Address0 = Unsafe.ReadUnaligned<ulong>(ref key);
        target->Address1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref key, sizeof(ulong)));
        target->Address2 = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref key, 2 * sizeof(ulong)));
        target->Slot = slot;
        target->Value = found ? value : default;
        EndWrite(target, unlocked);
        return result;
    }

    /// <summary>Drops a cached slot read.</summary>
    /// <returns><c>true</c> if the slot was cached.</returns>
    public bool RemoveNoLock(ulong hash, Address address, in UInt256 slot)
    {
        Entry* set = SetOf(hash);
        uint tag = TagOf(hash);
        uint epoch = _epoch;
        ref byte key = ref MemoryMarshal.GetReference(address.Bytes);

        for (int way = 0; way < Ways; way++)
        {
            Entry* entry = set + way;
            if (entry->Epoch == epoch && (entry->Header & TagMask) == tag && KeyEquals(entry, ref key, slot))
            {
                Invalidate(entry);
                _writer.Count--;
                return true;
            }
        }

        return false;
    }

    /// <summary>Drops every cached slot read in O(1) by moving to the next epoch.</summary>
    public void ClearNoLock()
    {
        _writer.Count = 0;
        uint epoch = _epoch;
        if (epoch != uint.MaxValue)
        {
            Volatile.Write(ref _epoch, epoch + 1);
            return;
        }

        // The epoch wraps, and any entry still stamped with FirstEpoch would read as current again.
        for (nuint i = 0; i < (nuint)Capacity; i++)
        {
            Entry* entry = _entries + i;
            if (entry->Epoch != EmptyEpoch) Invalidate(entry);
        }

        Volatile.Write(ref _epoch, FirstEpoch);
    }

    protected override void CleanUp()
    {
        Free();
        GC.SuppressFinalize(this);
    }

    private void Free()
    {
        if (Interlocked.Exchange(ref _freed, 1) != 0) return;
        NativeMemory.Free(_allocation);
        GC.RemoveMemoryPressure(_allocatedBytes);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Entry* SetOf(ulong hash) => _entries + (hash & _setMask) * Ways;

    // The set index takes the low bits and the tag the top 16, so the two stay independent.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint TagOf(ulong hash) => (uint)(hash >> 32) & TagMask;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool AddressEquals(ulong address0, ulong address1, uint address2, ref byte key) =>
        ((address0 ^ Unsafe.ReadUnaligned<ulong>(ref key))
         | (address1 ^ Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref key, sizeof(ulong))))
         | (address2 ^ Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref key, 2 * sizeof(ulong))))) == 0;

    private static bool KeyEquals(Entry* entry, ref byte key, in UInt256 slot) =>
        AddressEquals(entry->Address0, entry->Address1, entry->Address2, ref key) && entry->Slot == slot;

    private static void Invalidate(Entry* entry)
    {
        uint unlocked = BeginWrite(entry, entry->Header & TagMask);
        entry->Epoch = EmptyEpoch;
        EndWrite(entry, unlocked);
    }

    /// <summary>Marks an entry as being written; returns the header <see cref="EndWrite"/> publishes.</summary>
    /// <remarks>
    /// The version moves on every write, so a reader whose copy spans one sees a different header. It has 15 bits:
    /// repeating a header takes 32,768 writes to one entry within a single read, and one entry takes a few writes a
    /// second under load.
    /// </remarks>
    private static uint BeginWrite(Entry* entry, uint tag)
    {
        uint unlocked = tag | ((entry->Header + VersionIncrement) & VersionMask);
        // A full fence: none of the field stores that follow may become visible before the lock bit.
        Interlocked.Exchange(ref entry->Header, unlocked | LockBit);
        return unlocked;
    }

    private static void EndWrite(Entry* entry, uint unlocked) => Volatile.Write(ref entry->Header, unlocked);

    public enum AddResult
    {
        AlreadyPresent,
        Added,
        Replaced,
    }

    /// <summary>
    /// One cached read: the header, key and epoch share the first cache line, so a probe that misses reads one line
    /// per way.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = Size)]
    private struct Entry
    {
        public const int Size = 128;

        /// <summary>Tag (bits 16-31), version (bits 1-15) and <see cref="LockBit"/>.</summary>
        [FieldOffset(0)] public uint Header;
        [FieldOffset(4)] public uint Epoch;
        [FieldOffset(8)] public ulong Address0;
        [FieldOffset(16)] public ulong Address1;
        [FieldOffset(24)] public uint Address2;
        [FieldOffset(28)] public uint Found;
        [FieldOffset(32)] public UInt256 Slot;
        [FieldOffset(64)] public UInt256 Value;
    }

    /// <summary>
    /// State only the writer touches, padded by 128 bytes on each side so that the stores of every insert do not share
    /// a cache line with the fields lock-free readers load.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 264)]
    private struct WriterState
    {
        [FieldOffset(128)] public int Count;
        [FieldOffset(132)] public uint VictimCursor;
    }
}
