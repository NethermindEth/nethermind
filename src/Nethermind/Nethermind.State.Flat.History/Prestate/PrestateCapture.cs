// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.State.Flat.History.Prestate;

/// <summary>The parent values one block execution read, owned by the thread that runs that execution and reused
/// from block to block.</summary>
/// <remarks>
/// Soundness rule: a value is kept only if the scope returned it before any write to that key, any write to its
/// account (for the account) or any clear of its account's storage (for a slot) reached the scope. The scope was
/// opened at the parent and nothing else writes into it, so such a value is the parent value, whatever the world
/// state caches above the scope. Only the first scope opened while armed is recorded, and nothing after it commits or
/// closes: a read-only replay never commits, so the close ends the block.
/// </remarks>
public sealed class PrestateCapture
{
    private const byte Read = 1;
    private const byte Written = 2;
    private const byte StorageCleared = 4;

    private readonly Dictionary<AddressAsKey, AccountCapture> _accounts = [];
    private readonly Dictionary<StorageCell, SlotCapture> _slots = [];
    private bool _armed;
    private bool _scopeOpened;
    private bool _recording;
    private bool _finished;
    private string? _failure;
    private StateId _parent;

    /// <summary>The parent state of the recorded execution; valid once <see cref="IsComplete"/>.</summary>
    public StateId Parent => _parent;

    /// <summary>One scope was opened, recorded to its end, and nothing outside the soundness rule was asked of it.</summary>
    public bool IsComplete => _scopeOpened && _finished && _failure is null;

    /// <summary>Why the capture cannot be used, or null.</summary>
    public string? Failure => _failure ?? (!_scopeOpened ? "no scope was opened" : !_finished ? "the scope did not end" : null);

    /// <summary>Wraps the scope provider of an execution environment so that the scopes it opens report here.</summary>
    public IWorldStateScopeProvider Decorate(IWorldStateScopeProvider inner) => new PrestateCaptureScopeProvider(inner, this);

    /// <summary>Starts a capture: the next scope opened is recorded.</summary>
    public void Arm()
    {
        Reset();
        _armed = true;
    }

    /// <summary>Drops everything captured; called on every exit, so no key leaks into the next block.</summary>
    public void Reset()
    {
        _accounts.Clear();
        _slots.Clear();
        _armed = false;
        _scopeOpened = false;
        _recording = false;
        _finished = false;
        _failure = null;
        _parent = default;
    }

    internal bool OnScopeOpened(in StateId parent)
    {
        if (!_armed) return false;
        if (_scopeOpened)
        {
            Fail("a second scope was opened during one capture");
            return false;
        }

        _scopeOpened = true;
        _recording = true;
        _parent = parent;
        return true;
    }

    internal void OnScopeClosed() => Finish();

    internal void OnCommit() => Finish();

    private void Finish()
    {
        if (!_recording) return;
        _recording = false;
        _finished = true;
    }

    internal void Fail(string reason)
    {
        _failure ??= reason;
        _recording = false;
    }

    internal void OnAccountRead(Address address, Account? account)
    {
        if (!_recording) return;
        ref AccountCapture entry = ref CollectionsMarshal.GetValueRefOrAddDefault(_accounts, address, out _);
        if ((entry.Flags & (Read | Written)) != 0) return;
        entry.Value = account;
        entry.Flags |= Read;
    }

    internal void OnSlotRead(Address address, in UInt256 index, in UInt256 value)
    {
        if (!_recording) return;
        if (_accounts.TryGetValue(address, out AccountCapture account) && (account.Flags & StorageCleared) != 0) return;
        ref SlotCapture entry = ref CollectionsMarshal.GetValueRefOrAddDefault(_slots, new StorageCell(address, index), out _);
        if (entry.Flags != 0) return;
        entry.Value = value;
        entry.Flags = Read;
    }

    internal void OnAccountWritten(Address address, bool removedWithStorage)
    {
        if (!_recording) return;
        ref AccountCapture entry = ref CollectionsMarshal.GetValueRefOrAddDefault(_accounts, address, out _);
        entry.Flags |= removedWithStorage ? (byte)(Written | StorageCleared) : Written;
    }

    internal void OnSlotWritten(Address address, in UInt256 index)
    {
        if (!_recording) return;
        ref SlotCapture entry = ref CollectionsMarshal.GetValueRefOrAddDefault(_slots, new StorageCell(address, index), out _);
        entry.Flags |= Written;
        OnAccountWritten(address, removedWithStorage: false);
    }

    internal void OnStorageCleared(Address address) => OnAccountWritten(address, removedWithStorage: true);

    /// <summary>Encodes the captured values as a record of the block <paramref name="blockNumber"/> with hash
    /// <paramref name="blockHash"/>. The buffer is rented; the caller returns it. False, with nothing rented, when the
    /// record would exceed <paramref name="maxBytes"/>.</summary>
    public bool TryEncode(ulong blockNumber, in ValueHash256 blockHash, int maxBytes, out byte[]? buffer, out int length)
    {
        buffer = null;
        length = 0;
        CapturedSlot[] slots = ArrayPool<CapturedSlot>.Shared.Rent(_slots.Count);
        CapturedAccount[] accounts = ArrayPool<CapturedAccount>.Shared.Rent(_accounts.Count + _slots.Count);
        try
        {
            int slotCount = CollectSlots(slots);
            int accountCount = CollectAccounts(accounts, slots.AsSpan(0, slotCount));
            long measured = Measure(accounts.AsSpan(0, accountCount), slots.AsSpan(0, slotCount));
            if (measured > maxBytes) return false;

            byte[] rented = ArrayPool<byte>.Shared.Rent((int)measured);
            int written = Encode(rented, accounts.AsSpan(0, accountCount), slots.AsSpan(0, slotCount), _parent, blockNumber, blockHash);
            buffer = rented;
            length = written;
            return true;
        }
        finally
        {
            ArrayPool<CapturedSlot>.Shared.Return(slots, clearArray: true);
            ArrayPool<CapturedAccount>.Shared.Return(accounts, clearArray: true);
        }
    }

    private int CollectSlots(CapturedSlot[] slots)
    {
        int count = 0;
        foreach (KeyValuePair<StorageCell, SlotCapture> slot in _slots)
        {
            if (slot.Value.Flags != Read) continue;
            slots[count++] = new CapturedSlot(slot.Key.Address, slot.Key.Index, slot.Value.Value);
        }

        slots.AsSpan(0, count).Sort(new SlotOrder());
        return count;
    }

    /// <summary>Every read account, plus every address whose slots were read without its account value.</summary>
    private int CollectAccounts(CapturedAccount[] accounts, ReadOnlySpan<CapturedSlot> slots)
    {
        int count = 0;
        foreach (KeyValuePair<AddressAsKey, AccountCapture> account in _accounts)
        {
            if ((account.Value.Flags & Read) == 0) continue;
            accounts[count++] = new CapturedAccount(account.Key, account.Value.Value, Recorded: true);
        }

        for (int i = 0; i < slots.Length; i++)
        {
            Address address = slots[i].Address;
            if (i > 0 && slots[i - 1].Address == address) continue;
            if (_accounts.TryGetValue(address, out AccountCapture entry) && (entry.Flags & Read) != 0) continue;
            accounts[count++] = new CapturedAccount(address, null, Recorded: false);
        }

        accounts.AsSpan(0, count).Sort(new AccountOrder());
        return count;
    }

    [SkipLocalsInit]
    private static int Encode(Span<byte> destination, ReadOnlySpan<CapturedAccount> accounts, ReadOnlySpan<CapturedSlot> slots, in StateId parent,
        ulong blockNumber, in ValueHash256 blockHash)
    {
        Span<byte> header = destination[..PrestateRecordFormat.HeaderLength];
        header.Clear();
        header[PrestateRecordFormat.VersionOffset] = PrestateRecordFormat.Version;
        header[PrestateRecordFormat.FlagsOffset] = PrestateRecordFormat.RecordFlagNonAuthoritativeRoots;
        BinaryPrimitives.WriteUInt32LittleEndian(header[PrestateRecordFormat.AccountCountOffset..], (uint)accounts.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header[PrestateRecordFormat.SlotCountOffset..], (uint)slots.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(header[PrestateRecordFormat.ParentNumberOffset..], parent.BlockNumber);
        parent.StateRoot.Bytes.CopyTo(header[PrestateRecordFormat.ParentRootOffset..]);
        BinaryPrimitives.WriteUInt64LittleEndian(header[PrestateRecordFormat.BlockNumberOffset..], blockNumber);
        blockHash.Bytes.CopyTo(header[PrestateRecordFormat.BlockHashOffset..]);

        Span<byte> scratch = stackalloc byte[32];
        int position = PrestateRecordFormat.HeaderLength;
        int slot = 0;
        for (int i = 0; i < accounts.Length; i++)
        {
            int firstSlot = slot;
            Address address = accounts[i].Address;
            while (slot < slots.Length && slots[slot].Address == address) slot++;
            int slotCount = slot - firstSlot;

            position = WriteAccount(destination, position, accounts[i], slotCount > 0, scratch);
            if (slotCount == 0) continue;

            position += PrestateRecordFormat.WriteVarInt(destination[position..], (ulong)slotCount);
            for (int s = firstSlot; s < slot; s++)
            {
                slots[s].Index.ToBigEndian(destination.Slice(position, 32));
                position += 32;
                position = WriteMinimal(destination, position, slots[s].Value, scratch);
            }
        }

        return position;
    }

    /// <summary>The exact length <see cref="Encode"/> writes, so the size cap is checked before anything is rented.</summary>
    private static long Measure(ReadOnlySpan<CapturedAccount> accounts, ReadOnlySpan<CapturedSlot> slots)
    {
        long length = PrestateRecordFormat.HeaderLength;
        int slot = 0;
        for (int i = 0; i < accounts.Length; i++)
        {
            int firstSlot = slot;
            Address address = accounts[i].Address;
            while (slot < slots.Length && slots[slot].Address == address)
            {
                length += 32 + 1 + MinimalLength(slots[slot].Value);
                slot++;
            }

            length += Address.Size + 1;
            if (slot > firstSlot) length += VarIntLength((ulong)(slot - firstSlot));
            if (!accounts[i].Recorded || accounts[i].Value is not { } value) continue;

            length += VarIntLength(value.Nonce) + 1 + MinimalLength(value.Balance);
            if (value.HasCode) length += Hash256.Size;
            if (value.HasStorage) length += Hash256.Size;
        }

        return length;
    }

    private static int VarIntLength(ulong value)
    {
        int length = 1;
        while (value >= 0x80)
        {
            value >>= 7;
            length++;
        }

        return length;
    }

    private static int MinimalLength(in UInt256 value) =>
        value.u3 != 0 ? 32 - (BitOperations.LeadingZeroCount(value.u3) >> 3)
        : value.u2 != 0 ? 24 - (BitOperations.LeadingZeroCount(value.u2) >> 3)
        : value.u1 != 0 ? 16 - (BitOperations.LeadingZeroCount(value.u1) >> 3)
        : value.u0 != 0 ? 8 - (BitOperations.LeadingZeroCount(value.u0) >> 3)
        : 0;

    private static int WriteAccount(Span<byte> destination, int position, in CapturedAccount account, bool hasSlots, Span<byte> scratch)
    {
        account.Address.Bytes.CopyTo(destination[position..]);
        position += Address.Size;
        int flagsAt = position++;
        byte flags = hasSlots ? PrestateRecordFormat.AccountHasSlots : (byte)0;
        if (!account.Recorded)
        {
            destination[flagsAt] = (byte)(flags | PrestateRecordFormat.AccountValueNotRecorded);
            return position;
        }

        if (account.Value is not { } value)
        {
            destination[flagsAt] = flags;
            return position;
        }

        flags |= PrestateRecordFormat.AccountExists;
        position += PrestateRecordFormat.WriteVarInt(destination[position..], value.Nonce);
        position = WriteMinimal(destination, position, value.Balance, scratch);
        if (value.HasCode)
        {
            flags |= PrestateRecordFormat.AccountHasCode;
            value.CodeHash.Bytes.CopyTo(destination[position..]);
            position += Hash256.Size;
        }

        if (value.HasStorage)
        {
            flags |= PrestateRecordFormat.AccountHasStorageRoot;
            value.StorageRoot.Bytes.CopyTo(destination[position..]);
            position += Hash256.Size;
        }

        destination[flagsAt] = flags;
        return position;
    }

    private static int WriteMinimal(Span<byte> destination, int position, in UInt256 value, Span<byte> scratch)
    {
        value.ToBigEndian(scratch);
        int start = scratch.IndexOfAnyExcept((byte)0);
        int length = start < 0 ? 0 : 32 - start;
        destination[position++] = (byte)length;
        if (length > 0) scratch[start..].CopyTo(destination[position..]);
        return position + length;
    }

    private struct AccountCapture
    {
        public Account? Value;
        public byte Flags;
    }

    private struct SlotCapture
    {
        public UInt256 Value;
        public byte Flags;
    }

    private readonly record struct CapturedAccount(Address Address, Account? Value, bool Recorded);

    private readonly record struct CapturedSlot(Address Address, UInt256 Index, UInt256 Value);

    private readonly struct AccountOrder : IComparer<CapturedAccount>
    {
        public int Compare(CapturedAccount x, CapturedAccount y) => x.Address.Bytes.SequenceCompareTo(y.Address.Bytes);
    }

    private readonly struct SlotOrder : IComparer<CapturedSlot>
    {
        public int Compare(CapturedSlot x, CapturedSlot y)
        {
            int byAddress = x.Address.Bytes.SequenceCompareTo(y.Address.Bytes);
            return byAddress != 0 ? byAddress : x.Index.CompareTo(y.Index);
        }
    }
}
