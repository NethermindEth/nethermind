// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.StateDiffArchive.Data;

/// <summary>
/// Accumulates one block's account/storage/code writes (teed from the world-state write batch), grouped by
/// address, and serializes them to the RLP wire format consumed by <see cref="StateDiffRecord"/>.
/// </summary>
/// <remarks>
/// Wire format (positional; the leading version byte allows forward-compatible additions):
/// <code>
/// StateDiffRecord = [
///   Version     (byte),
///   BlockNumber (uint64),
///   StateRoot   (32B),
///   Accounts = [
///     [ Address (20B), Change (byte 0|1|2), Account (RLP account, only when Change==Set),
///       StorageCleared (bool), Slots = [ [ Index (uint256), Value (bytes) ], ... ] ],
///     ...
///   ],
///   Codes = [ [ CodeHash (32B), Code (bytes) ], ... ]
/// ]
/// </code>
/// </remarks>
public sealed class StateDiffRecordBuilder
{
    private static readonly AccountDecoder AccountRlp = AccountDecoder.Instance;

    private readonly Dictionary<Address, Entry> _accounts = [];
    private readonly List<(ValueHash256 Hash, byte[] Code)> _codes = [];

    public void SetAccount(Address address, Account? account)
    {
        Entry entry = GetOrAdd(address);
        entry.Change = account is null ? AccountChangeKind.Deleted : AccountChangeKind.Set;
        entry.Account = account;
    }

    public void ClearStorage(Address address) => GetOrAdd(address).StorageCleared = true;

    public void SetSlot(Address address, in UInt256 index, byte[] value)
        => (GetOrAdd(address).Slots ??= []).Add((index, value));

    public void AddCode(in ValueHash256 codeHash, byte[] code) => _codes.Add((codeHash, code));

    public void Reset()
    {
        _accounts.Clear();
        _codes.Clear();
    }

    public int GetLength(ulong blockNumber, Hash256 stateRoot) => Rlp.LengthOfSequence(GetContentLength(blockNumber, stateRoot));

    public void WriteTo<TWriter>(ref TWriter w, ulong blockNumber, Hash256 stateRoot)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        w.StartSequence(GetContentLength(blockNumber, stateRoot));
        w.Encode(StateDiffRecord.CurrentVersion);
        w.Encode(blockNumber);
        w.Encode(stateRoot);

        w.StartSequence(GetAccountsContentLength());
        foreach ((Address address, Entry entry) in _accounts)
        {
            w.StartSequence(GetAccountDiffContentLength(entry));
            w.Encode(address);
            w.Encode((byte)entry.Change);
            if (entry.Change == AccountChangeKind.Set) AccountRlp.Encode(ref w, entry.Account);
            w.Encode(entry.StorageCleared);

            w.StartSequence(GetSlotsContentLength(entry.Slots));
            if (entry.Slots is not null)
            {
                foreach ((UInt256 index, byte[] value) in entry.Slots)
                {
                    w.StartSequence(GetSlotContentLength(index, value));
                    w.Encode(index);
                    w.Encode(value);
                }
            }
        }

        w.StartSequence(GetCodesContentLength());
        foreach ((ValueHash256 hash, byte[] code) in _codes)
        {
            w.StartSequence(GetCodeContentLength(code));
            w.Encode(hash);
            w.Encode(code);
        }
    }

    private int GetContentLength(ulong blockNumber, Hash256 stateRoot)
        => Rlp.LengthOf(StateDiffRecord.CurrentVersion)
           + Rlp.LengthOf(blockNumber)
           + Rlp.LengthOf(stateRoot)
           + Rlp.LengthOfSequence(GetAccountsContentLength())
           + Rlp.LengthOfSequence(GetCodesContentLength());

    private int GetAccountsContentLength()
    {
        int total = 0;
        foreach (Entry entry in _accounts.Values) total += Rlp.LengthOfSequence(GetAccountDiffContentLength(entry));
        return total;
    }

    private static int GetAccountDiffContentLength(Entry entry)
    {
        int length = Rlp.LengthOf(Address.Zero)
                     + Rlp.LengthOf((byte)entry.Change)
                     + Rlp.LengthOf((byte)(entry.StorageCleared ? 1 : 0))
                     + Rlp.LengthOfSequence(GetSlotsContentLength(entry.Slots));
        if (entry.Change == AccountChangeKind.Set) length += AccountRlp.GetLength(entry.Account);
        return length;
    }

    private static int GetSlotsContentLength(List<(UInt256 Index, byte[] Value)>? slots)
    {
        if (slots is null) return 0;
        int total = 0;
        foreach ((UInt256 index, byte[] value) in slots) total += Rlp.LengthOfSequence(GetSlotContentLength(index, value));
        return total;
    }

    private static int GetSlotContentLength(in UInt256 index, byte[] value) => Rlp.LengthOf(index) + Rlp.LengthOf(value);

    private int GetCodesContentLength()
    {
        int total = 0;
        foreach ((ValueHash256 _, byte[] code) in _codes) total += Rlp.LengthOfSequence(GetCodeContentLength(code));
        return total;
    }

    private static int GetCodeContentLength(byte[] code) => Rlp.LengthOfKeccakRlp + Rlp.LengthOf(code);

    private Entry GetOrAdd(Address address)
    {
        if (!_accounts.TryGetValue(address, out Entry? entry))
        {
            entry = new Entry();
            _accounts[address] = entry;
        }
        return entry;
    }

    private sealed class Entry
    {
        public AccountChangeKind Change = AccountChangeKind.None;
        public Account? Account;
        public bool StorageCleared;
        public List<(UInt256 Index, byte[] Value)>? Slots;
    }
}
