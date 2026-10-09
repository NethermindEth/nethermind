// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.State.Flat.History.Changesets;

/// <summary>What the transactions before a given one wrote, folded in execution order. A read that misses belongs to
/// the state as of the previous block, which the existing history answers.</summary>
internal sealed class MidBlockOverlay
{
    private readonly Dictionary<AddressAsKey, AccountOverlay> _accounts = [];
    private readonly Dictionary<StorageCell, StorageWrite> _storage = [];
    private readonly HashSet<AddressAsKey> _storageAccounts = [];

    public ulong Block { get; private set; }

    /// <summary>The block whose rows were folded, by hash. A height is not an identity: capture can replace the rows
    /// of a height with a sibling's, and a prefix folded from the one is not a prefix of the other.</summary>
    public ValueHash256 Hash { get; private set; }

    /// <summary>The first transaction this overlay does not yet include.</summary>
    public ushort Folded { get; private set; }

    internal int Pins { get; set; }

    internal bool Extending { get; set; }
    internal long Version { get; set; }

    internal Dictionary<AddressAsKey, AccountOverlay>.Enumerator Accounts => _accounts.GetEnumerator();

    internal Dictionary<StorageCell, StorageWrite>.Enumerator Writes => _storage.GetEnumerator();

    public void Reset(ulong block) => Reset(block, default);

    public void Reset(ulong block, in ValueHash256 hash)
    {
        _accounts.Clear();
        _storage.Clear();
        _storageAccounts.Clear();
        Block = block;
        Hash = hash;
        Folded = 0;
    }

    public void Fold(ushort transactionIndex, ReadOnlySpan<byte> changeset)
    {
        ChangesetCodec.Enumerator entries = ChangesetCodec.Read(changeset);
        while (entries.MoveNext())
        {
            if (entries.Kind == ChangesetCodec.AccountKind) FoldAccount(ref entries, transactionIndex);
            else FoldStorage(ref entries, transactionIndex);
        }

        Folded = (ushort)(transactionIndex + 1);
    }

    public bool TryGetAccount(Address address, [NotNullWhen(true)] out AccountOverlay? overlay) => _accounts.TryGetValue(address, out overlay);

    /// <summary>Whether the prefix wrote or wiped any slot of the account.</summary>
    public bool HasStorage(Address address) =>
        _storageAccounts.Contains(address) || (_accounts.TryGetValue(address, out AccountOverlay? account) && account.StorageClearedAt != NeverCleared);

    /// <summary>Whether the prefix wrote any slot of the account, wipe or no wipe. A write recorded before a wipe in
    /// the same block does not survive it, so this errs towards saying there is storage, which is the safe direction
    /// for a reader deciding whether a wipe has anything to clear.</summary>
    public bool HasStorageWrites(Address address) => _storageAccounts.Contains(address);

    /// <summary>A wiped account answers zero even where the overlay holds no write for the slot: the wipe applies to
    /// every slot the account held, not only to those the block touched.</summary>
    public bool TryGetStorage(in StorageCell cell, out UInt256 value)
    {
        int clearedAt = _accounts.TryGetValue(cell.Address, out AccountOverlay? account) ? account.StorageClearedAt : NeverCleared;
        if (_storage.TryGetValue(cell, out StorageWrite write) && write.Transaction >= clearedAt)
        {
            value = write.Value;
            return true;
        }

        value = UInt256.Zero;
        return clearedAt != NeverCleared;
    }

    internal const int NeverCleared = -1;

    private void FoldStorage(ref ChangesetCodec.Enumerator entries, ushort transactionIndex)
    {
        StorageCell cell = new(new Address(entries.Address), new UInt256(entries.Index, isBigEndian: true));
        _storage[cell] = new StorageWrite(transactionIndex, new UInt256(entries.Value, isBigEndian: true));
        _storageAccounts.Add(cell.Address);
    }

    private void FoldAccount(ref ChangesetCodec.Enumerator entries, ushort transactionIndex)
    {
        Address address = new(entries.Address);
        if (!_accounts.TryGetValue(address, out AccountOverlay? overlay))
        {
            overlay = new AccountOverlay();
            _accounts[address] = overlay;
        }

        if (entries.Deleted)
        {
            overlay.Balance = null;
            overlay.Nonce = null;
            overlay.CodeHash = null;
            overlay.Emptied = true;
            overlay.Exists = false;
        }

        if (entries.StorageCleared) overlay.StorageClearedAt = transactionIndex;

        if (!entries.Balance.IsEmpty)
        {
            overlay.Balance = new UInt256(entries.Balance, isBigEndian: true);
            overlay.Exists = true;
        }

        if (!entries.Nonce.IsEmpty)
        {
            overlay.Nonce = new UInt256(entries.Nonce, isBigEndian: true);
            overlay.Exists = true;
        }

        if (entries.CodeHash.IsEmpty) return;

        overlay.CodeHash = new ValueHash256(entries.CodeHash);
        overlay.Exists = true;
    }

    internal readonly struct StorageWrite(ushort transaction, in UInt256 value)
    {
        public int Transaction { get; } = transaction;

        public UInt256 Value { get; } = value;
    }

    /// <summary>The fields a transaction changed. A field left null takes the value the account had at the previous
    /// block, unless <see cref="Emptied"/> says the account was deleted, when it takes the empty account's value.</summary>
    internal sealed class AccountOverlay
    {
        public UInt256? Balance { get; set; }

        public UInt256? Nonce { get; set; }

        public ValueHash256? CodeHash { get; set; }

        public bool Emptied { get; set; }

        public bool Exists { get; set; } = true;

        public int StorageClearedAt { get; set; } = NeverCleared;
    }
}
