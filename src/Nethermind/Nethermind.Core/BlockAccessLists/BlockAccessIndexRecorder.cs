// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using static Nethermind.Core.BlockAccessLists.BlockAccessListLog;

namespace Nethermind.Core.BlockAccessLists;

/// <summary>
/// Records the state accesses of the block access index currently executing into a <see cref="BlockAccessListLog"/>,
/// and answers the index's own reads of what it wrote.
/// </summary>
/// <remarks>
/// A key goes to the log as an access when the index first touches it, and the writes stay with the recorder until
/// <see cref="End"/> adds the net changes, so the accesses of reverted calls stay listed, as EIP-7928 requires. Each
/// written key maps to its latest write, which also points to the key's value from before the index. A write overwrites
/// that entry in place when no snapshot was taken since, so a loop writing one slot keeps one entry per snapshot.
/// <see cref="Restore"/> marks the writes after the snapshot as reverted. The lookups report a key only while its latest
/// value differs from the value before the index, except nonces, which only move forward.
/// </remarks>
public sealed class BlockAccessIndexRecorder : IJournal<int>
{
    private readonly Dictionary<EntryKey, int> _latestWrites = [];
    private readonly Dictionary<StorageCell, UInt256> _seededPreTxStorage = new(StorageCell.EqualityComparer);
    private readonly List<Entry> _writes = [];
    private readonly List<UInt256> _preTxValues = [];
    private readonly List<CodeWrite> _codeWrites = [];
    private int _start;
    private Address? _lastAccount;
    private ulong _snapshotEpoch;

    /// <summary>The log the index's entries go to; the indexes sharing it must not run concurrently.</summary>
    public BlockAccessListLog Log { get; set; } = new();

    /// <summary>The block access index the recorded changes belong to.</summary>
    public uint Index { get; private set; }

    /// <summary>Starts recording <paramref name="index"/> at the end of <see cref="Log"/>.</summary>
    /// <remarks>The changes of a previous index that did not <see cref="End"/> are dropped.</remarks>
    public void Begin(uint index)
    {
        ClearIndexState();
        Index = index;
        _start = Log.Count;
    }

    /// <summary>The position in <see cref="Log"/> where the current index starts.</summary>
    public int Start => _start;

    /// <summary>Drops everything the current index recorded, such as for a transaction left out of a block.</summary>
    public void Discard()
    {
        Log.Truncate(_start);
        ClearIndexState();
    }

    /// <summary>Adds the net changes of the current index to <see cref="Log"/>; call once the index has executed.</summary>
    public void End()
    {
        AddChanges(Log);
        ClearIndexState();
    }

    /// <summary>Adds the net changes of the current index to <paramref name="log"/>.</summary>
    /// <remarks>
    /// A key's latest write is its last one that was not reverted. It is a change only when its value differs from the
    /// value before the index; a slot set back to its value, or whose writes all reverted, stays the read its first write
    /// logged.
    /// </remarks>
    internal void AddChanges(BlockAccessListLog log)
    {
        ReadOnlySpan<Entry> writes = CollectionsMarshal.AsSpan(_writes);
        foreach (KeyValuePair<EntryKey, int> latest in _latestWrites)
        {
            EntryKey key = latest.Key;
            ref readonly Entry write = ref writes[latest.Value];
            Section section;
            UInt256 value = write.Value;
            switch (key.Kind)
            {
                case EntryKind.Balance when write.Value != _preTxValues[write.PreTxValuePosition]:
                    section = Section.Balance;
                    break;
                case EntryKind.Nonce when write.HasValue:
                    section = Section.Nonce;
                    break;
                case EntryKind.Code when write.HasValue && IsCodeChanged(_codeWrites[(int)write.Value.u0]):
                    section = Section.Code;
                    value = (ulong)log.AddCodeChange(_codeWrites[(int)write.Value.u0].Change);
                    break;
                case EntryKind.Storage when write.HasValue && write.Value != _preTxValues[write.PreTxValuePosition]:
                    section = Section.Storage;
                    break;
                default:
                    continue;
            }

            log.AddEntry(new Item(key.Cell.Address, section, in key.Cell.Index, in value, Index, isChange: true));
        }
    }

    /// <summary>The account's existence as last recorded by <see cref="RecordAccountExistence"/>, or null if never recorded.</summary>
    public bool? GetAccountExists(Address address)
        => TryGetLatest(new EntryKey(address, EntryKind.Existence), out Entry entry) && entry.HasValue ? !entry.Value.IsZero : null;

    /// <summary>Gets the balance the index changed the account to.</summary>
    public bool TryGetBalance(Address address, out UInt256 balance)
    {
        bool found = TryGetLatest(new EntryKey(address, EntryKind.Balance), out Entry entry) && entry.Value != _preTxValues[entry.PreTxValuePosition];
        balance = found ? entry.Value : default;
        return found;
    }

    /// <summary>Gets the account's balance from before the index, if the index changed it.</summary>
    public bool TryGetPreTxBalance(Address address, out UInt256 balance)
    {
        bool found = TryGetLatest(new EntryKey(address, EntryKind.Balance), out Entry entry) && entry.Value != _preTxValues[entry.PreTxValuePosition];
        balance = found ? _preTxValues[entry.PreTxValuePosition] : default;
        return found;
    }

    /// <summary>Gets the nonce the index last wrote.</summary>
    public bool TryGetNonce(Address address, out ulong nonce)
    {
        bool found = TryGetLatest(new EntryKey(address, EntryKind.Nonce), out Entry entry) && entry.HasValue;
        nonce = found ? entry.Value.u0 : 0;
        return found;
    }

    /// <summary>Gets the code the index changed the account to.</summary>
    public bool TryGetCodeChange(Address address, out CodeChange codeChange)
    {
        bool found = TryGetCodeWrite(address, out CodeWrite codeWrite);
        codeChange = found ? codeWrite.Change : default;
        return found;
    }

    /// <summary>Gets the account's code from before the index, if the index changed it.</summary>
    public bool TryGetPreTxCode(Address address, out ReadOnlyMemory<byte> code)
    {
        bool found = TryGetCodeWrite(address, out CodeWrite codeWrite);
        code = found ? codeWrite.PreTxCode : default;
        return found;
    }

    /// <summary>Gets the slot value the index changed the slot to.</summary>
    public bool TryGetStorage(in StorageCell cell, out UInt256 value)
    {
        bool found = TryGetLatest(new EntryKey(cell, EntryKind.Storage), out Entry entry) && entry.HasValue && entry.Value != _preTxValues[entry.PreTxValuePosition];
        value = found ? entry.Value : default;
        return found;
    }

    /// <summary>Gets the slot's value from before the index, if the index wrote the slot or a storage clear seeded it.</summary>
    public bool TryGetPreTxStorage(in StorageCell cell, out UInt256 value)
    {
        if (TryGetLatest(new EntryKey(cell, EntryKind.Storage), out Entry entry))
        {
            value = _preTxValues[entry.PreTxValuePosition];
            return true;
        }
        return _seededPreTxStorage.TryGetValue(cell, out value);
    }

    /// <summary>Adds the keys the index changed so far: accounts with a balance or code change, and changed slots.</summary>
    public void GetChanges(ICollection<Address> balances, ICollection<StorageCell> slots, ICollection<Address> codes)
    {
        foreach (KeyValuePair<EntryKey, int> latest in _latestWrites)
        {
            EntryKey key = latest.Key;
            switch (key.Kind)
            {
                case EntryKind.Balance when TryGetBalance(key.Cell.Address, out _):
                    balances.Add(key.Cell.Address);
                    break;
                case EntryKind.Storage when TryGetStorage(in key.Cell, out _):
                    slots.Add(key.Cell);
                    break;
                case EntryKind.Code when TryGetCodeChange(key.Cell.Address, out _):
                    codes.Add(key.Cell.Address);
                    break;
            }
        }
    }

    public void AddAccountRead(Address address) => AddAccount(address);

    public void AddStorageRead(in StorageCell cell)
    {
        AddAccount(cell.Address);
        Log.AddEntry(new Item(cell.Address, Section.Storage, in cell.Index, default, Index, isChange: false));
    }

    /// <summary>Records physical account existence for execution and snapshot restoration.</summary>
    public void RecordAccountExistence(Address address, bool exists)
    {
        AddAccount(address);
        if (GetAccountExists(address) == exists) return;

        ref Entry entry = ref AddWrite(new EntryKey(address, EntryKind.Existence), default);
        entry.Value = exists ? UInt256.One : UInt256.Zero;
        entry.HasValue = true;
    }

    public void AddBalanceChange(Address address, in UInt256 before, in UInt256 after)
    {
        // EIP-7928 lists touched-but-unchanged accounts, so a zero transfer still records the account.
        if (before == after)
        {
            AddAccount(address);
            return;
        }

        ref Entry entry = ref AddWrite(new EntryKey(address, EntryKind.Balance), in before);
        entry.Value = after;
        entry.HasValue = true;
    }

    public void AddNonceChange(Address address, ulong newNonce)
    {
        if (newNonce == 0) return;

        ref Entry entry = ref AddWrite(new EntryKey(address, EntryKind.Nonce), default);
        entry.Value = newNonce;
        entry.HasValue = true;
    }

    public void AddCodeChange(Address address, ReadOnlyMemory<byte> before, ReadOnlyMemory<byte> after)
        => AddCodeChange(address, before, after, adoptedCodeHash: null);

    /// <summary>Records code adopted by EIP-8298 <c>SETCODEFROM</c>, which the change records by its hash.</summary>
    public void AddAdoptedCodeChange(Address address, ReadOnlyMemory<byte> before, ReadOnlyMemory<byte> after, in ValueHash256 codeHash)
        => AddCodeChange(address, before, after, codeHash);

    private void AddCodeChange(Address address, ReadOnlyMemory<byte> before, ReadOnlyMemory<byte> after, ValueHash256? adoptedCodeHash)
    {
        if (before.Span.SequenceEqual(after.Span)) return;

        EntryKey key = new(address, EntryKind.Code);
        ReadOnlyMemory<byte> preTxCode = TryGetLatest(key, out Entry latest)
            ? _codeWrites[(int)latest.Value.u0].PreTxCode
            : before.IsNull() ? Array.Empty<byte>() : before;
        // EIP-8298: the form follows whichever write left the code, so a later adoption makes the change hash-only.
        CodeChange change = adoptedCodeHash is { } codeHash ? CodeChange.Adopted(Index, in codeHash, after.ToArray()) : new CodeChange(Index, after.ToArray());
        _codeWrites.Add(new CodeWrite(preTxCode, change));
        int position = _codeWrites.Count - 1;

        ref Entry entry = ref AddWrite(key, default);
        entry.Value = (ulong)position;
        entry.HasValue = true;
    }

    /// <summary>Records a slot's value from before the index when a storage clear hides it from the first write.</summary>
    public void SeedPreTxStorage(in StorageCell cell, in UInt256 value)
    {
        AddAccount(cell.Address);
        _seededPreTxStorage.TryAdd(cell, value);
    }

    public void AddStorageChange(in StorageCell cell, in UInt256 before, in UInt256 after)
    {
        if (before == after) return;

        UInt256 preTxValue = _seededPreTxStorage.Count != 0 && _seededPreTxStorage.TryGetValue(cell, out UInt256 seeded) ? seeded : before;
        ref Entry entry = ref AddWrite(new EntryKey(cell, EntryKind.Storage), in preTxValue);
        entry.Value = after;
        entry.HasValue = true;
    }

    /// <summary>Records SELFDESTRUCT: the account's slot changes become reads, its nonce and code changes go, and its balance drops to zero.</summary>
    public void DeleteAccount(Address address, in UInt256 oldBalance)
    {
        AddAccount(address);

        using ArrayPoolListRef<EntryKey> deleted = new(0);
        Span<Entry> entries = CollectionsMarshal.AsSpan(_writes);
        foreach (KeyValuePair<EntryKey, int> latest in _latestWrites)
        {
            EntryKey key = latest.Key;
            if (key.Kind is EntryKind.Storage or EntryKind.Nonce or EntryKind.Code
                && entries[latest.Value].HasValue
                && key.Cell.Address.Equals(address))
            {
                deleted.Add(key);
            }
        }

        foreach (EntryKey key in deleted.AsSpan())
        {
            AddWrite(key, default).HasValue = false;
        }

        AddBalanceChange(address, in oldBalance, UInt256.Zero);
    }

    public int TakeSnapshot()
    {
        _snapshotEpoch++;
        return _writes.Count;
    }

    public void Restore(int snapshot)
    {
        _snapshotEpoch++;
        Span<Entry> entries = CollectionsMarshal.AsSpan(_writes);
        for (int position = entries.Length - 1; position >= int.Max(0, snapshot); position--)
        {
            ref Entry entry = ref entries[position];
            if (entry.Reverted) continue;

            if (entry.Previous >= 0) _latestWrites[entry.Key] = entry.Previous;
            else _latestWrites.Remove(entry.Key);
            entry.Reverted = true;
        }
    }

    private void ClearIndexState()
    {
        _latestWrites.ClearAndTrim();
        _seededPreTxStorage.ClearAndTrim();
        _writes.ClearAndTrim();
        _preTxValues.ClearAndTrim();
        _codeWrites.ClearAndTrim();
        _lastAccount = null;
        _snapshotEpoch = 0;
    }

    private void AddAccount(Address address)
    {
        if (address.Equals(_lastAccount)) return;
        Log.AddEntry(new Item(address, Section.Account, default, default, Index, isChange: false));
        _lastAccount = address;
    }

    private bool TryGetCodeWrite(Address address, out CodeWrite codeWrite)
    {
        if (TryGetLatest(new EntryKey(address, EntryKind.Code), out Entry entry) && entry.HasValue)
        {
            codeWrite = _codeWrites[(int)entry.Value.u0];
            if (IsCodeChanged(codeWrite)) return true;
        }
        codeWrite = default;
        return false;
    }

    private bool TryGetLatest(in EntryKey key, out Entry entry)
    {
        if (_latestWrites.TryGetValue(key, out int position))
        {
            entry = _writes[position];
            return true;
        }
        entry = default;
        return false;
    }

    /// <summary>Returns the entry to fill for a write to <paramref name="key"/>.</summary>
    /// <param name="key">The written key.</param>
    /// <param name="preTxValue">The key's value from before the index, used when the index has not written it yet.</param>
    private ref Entry AddWrite(in EntryKey key, in UInt256 preTxValue)
    {
        AddAccount(key.Cell.Address);
        ref int latest = ref CollectionsMarshal.GetValueRefOrAddDefault(_latestWrites, key, out bool exists);
        Span<Entry> writes = CollectionsMarshal.AsSpan(_writes);
        if (exists)
        {
            ref Entry previous = ref writes[latest];
            if (previous.Epoch == _snapshotEpoch) return ref previous;

            // Adding to the writes leaves the dictionary alone, so the ref into it stays valid.
            _writes.Add(previous with { Epoch = _snapshotEpoch, Previous = latest });
        }
        else
        {
            // The write's access, which stays a read if the index leaves the slot as it found it.
            if (key.Kind == EntryKind.Storage)
                Log.AddEntry(new Item(key.Cell.Address, Section.Storage, in key.Cell.Index, default, Index, isChange: false));
            _preTxValues.Add(preTxValue);
            _writes.Add(new Entry { Key = key, PreTxValuePosition = _preTxValues.Count - 1, Epoch = _snapshotEpoch, Previous = -1 });
        }

        latest = _writes.Count - 1;
        return ref CollectionsMarshal.AsSpan(_writes)[latest];
    }

    private static bool IsCodeChanged(in CodeWrite codeWrite) => !codeWrite.Change.Code.AsSpan().SequenceEqual(codeWrite.PreTxCode.Span);

    private enum EntryKind : byte
    {
        Storage,
        Balance,
        Nonce,
        Code,
        Existence,
    }

    private readonly struct EntryKey(in StorageCell cell, EntryKind kind) : IEquatable<EntryKey>
    {
        public readonly StorageCell Cell = cell;
        public readonly EntryKind Kind = kind;

        public EntryKey(Address address, EntryKind kind) : this(new StorageCell(address, UInt256.Zero), kind) { }

        public bool Equals(EntryKey other) => Kind == other.Kind && Cell.Equals(in other.Cell);
        public override bool Equals(object? obj) => obj is EntryKey other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Cell.GetHashCode(), Kind);
    }

    private struct Entry
    {
        public EntryKey Key;
        /// <summary>The position of the key's value from before the index.</summary>
        public int PreTxValuePosition;
        /// <summary>The written value; for code, the position of its <see cref="CodeWrite"/>.</summary>
        public UInt256 Value;
        public ulong Epoch;
        /// <summary>The key's previous write, restored by a revert, or -1.</summary>
        public int Previous;
        /// <summary>False once a SELFDESTRUCT dropped the change.</summary>
        public bool HasValue;
        /// <summary>Whether a revert undid the write.</summary>
        public bool Reverted;
    }

    private readonly record struct CodeWrite(ReadOnlyMemory<byte> PreTxCode, CodeChange Change);
}
