// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Nethermind.Int256;

namespace Nethermind.Core.BlockAccessLists;

/// <summary>
/// Dense read-ordinal model over a suggested BAL: assigns every declared storage read a unique
/// global ordinal <c>0 &lt;= g &lt; <see cref="TotalReads"/></c>, built once per block and reused by
/// the prefetch destination, the pure-read path, and read-coverage validation.
/// </summary>
/// <remarks>
/// The ordinal is implied by position, never stored per slot: an account at index <c>a</c> owns the
/// contiguous range <c>[ReadBase(a), ReadBase(a) + readCount(a))</c>, and a declared read at local
/// index <c>j</c> within that account has global ordinal <c>ReadBase(a) + j</c>. Accounts are held in
/// the BAL's own (address-sorted) order, so the prefix-sum <c>ReadBase</c> matches the order callers
/// enumerate. The model covers declared <em>reads</em> only (<see cref="ReadOnlyAccountChanges.StorageReads"/>);
/// storage changes carry their own values in the BAL and are not part of the read ordinal space.
/// <para>
/// Slot lookup is tiered (<see cref="TryGetReadLocalIndex"/>): a monotonic cursor makes ascending-slot
/// streams amortized O(1), small read sets use a linear scan, larger ones a binary search over the
/// decoder-sorted <see cref="ReadOnlyAccountChanges.StorageReads"/>. No per-slot hash map is allocated.
/// </para>
/// </remarks>
public sealed class BalStorageReadPlan
{
    /// <summary>Read sets at or below this length use a linear scan; larger ones binary-search.</summary>
    private const int LinearScanThreshold = 16;

    private readonly AccountEntry[] _accounts;
    private readonly Dictionary<AddressAsKey, int> _addressToIndex;

    /// <summary>Total declared storage reads across all accounts (the size of the ordinal space).</summary>
    public int TotalReads { get; }

    /// <summary>Number of BAL accounts, indexable via <see cref="GetAccount"/>.</summary>
    public int AccountCount => _accounts.Length;

    private BalStorageReadPlan(AccountEntry[] accounts, Dictionary<AddressAsKey, int> addressToIndex, int totalReads)
    {
        _accounts = accounts;
        _addressToIndex = addressToIndex;
        TotalReads = totalReads;
    }

    /// <summary>
    /// Builds the read-ordinal model from a suggested BAL. Accounts keep the BAL's address-sorted
    /// order; <c>ReadBase</c> is the running prefix sum of per-account read counts.
    /// </summary>
    public static BalStorageReadPlan Build(ReadOnlyBlockAccessList bal)
    {
        ReadOnlySpan<ReadOnlyAccountChanges> accounts = bal.AccountChanges.AsSpan();
        AccountEntry[] entries = new AccountEntry[accounts.Length];
        Dictionary<AddressAsKey, int> addressToIndex = new(accounts.Length);

        int readBase = 0;
        for (int i = 0; i < accounts.Length; i++)
        {
            ReadOnlyAccountChanges account = accounts[i];
            AssertAscending(account.StorageReads);
            entries[i] = new AccountEntry(account, readBase);
            addressToIndex[account.Address] = i;
            readBase += account.StorageReads.Length;
        }

        // readBase has accumulated every account's read count -> the total ordinal space (== bal.TotalStorageReads).
        return new BalStorageReadPlan(entries, addressToIndex, readBase);
    }

    /// <summary>The account at <paramref name="accountIndex"/> (BAL address-sorted order).</summary>
    public AccountEntry GetAccount(int accountIndex) => _accounts[accountIndex];

    /// <summary>Resolves <paramref name="address"/> to its account index, or false if the BAL omits it.</summary>
    public bool TryGetAccountIndex(Address address, out int accountIndex)
        => _addressToIndex.TryGetValue(address, out accountIndex);

    /// <summary>Global read ordinal of the read at <paramref name="localIndex"/> in <paramref name="accountIndex"/>.</summary>
    public int GlobalReadOrdinal(int accountIndex, int localIndex) => _accounts[accountIndex].ReadBase + localIndex;

    /// <summary>
    /// Finds the local index of <paramref name="slot"/> within the account's declared reads.
    /// </summary>
    /// <param name="cursor">
    /// Caller-owned position carried across a same-account read stream; pass <c>-1</c> to start. Ascending
    /// streams advance it by one and hit immediately, so the common case avoids any search.
    /// </param>
    /// <returns><c>true</c> and the local index if the slot is a declared read; otherwise <c>false</c>.</returns>
    public bool TryGetReadLocalIndex(int accountIndex, in UInt256 slot, ref int cursor, out int localIndex)
    {
        ReadOnlySpan<UInt256> reads = _accounts[accountIndex].Account.StorageReads;

        // Monotonic cursor: ascending-slot streams (the bloat fixture) hit here with no search.
        int next = cursor + 1;
        if ((uint)next < (uint)reads.Length && slot.Equals(reads[next]))
        {
            cursor = next;
            localIndex = next;
            return true;
        }

        int found = reads.Length <= LinearScanThreshold ? LinearScan(reads, in slot) : reads.BinarySearch(slot);
        if (found >= 0)
        {
            cursor = found;
            localIndex = found;
            return true;
        }

        localIndex = -1;
        return false;
    }

    /// <summary>Global read ordinal for <paramref name="slot"/> on <paramref name="address"/>, or false if not a declared read.</summary>
    /// <remarks>Convenience for callers without a same-account cursor; resolves the account then the slot once.</remarks>
    public bool TryGetGlobalReadOrdinal(Address address, in UInt256 slot, out int globalOrdinal)
    {
        if (_addressToIndex.TryGetValue(address, out int accountIndex))
        {
            int cursor = -1;
            if (TryGetReadLocalIndex(accountIndex, in slot, ref cursor, out int localIndex))
            {
                globalOrdinal = _accounts[accountIndex].ReadBase + localIndex;
                return true;
            }
        }

        globalOrdinal = -1;
        return false;
    }

    private static int LinearScan(ReadOnlySpan<UInt256> reads, in UInt256 slot)
    {
        for (int i = 0; i < reads.Length; i++)
        {
            if (reads[i].Equals(slot)) return i;
        }
        return -1;
    }

    [Conditional("DEBUG")]
    private static void AssertAscending(ReadOnlySpan<UInt256> reads)
    {
        for (int i = 1; i < reads.Length; i++)
        {
            Debug.Assert(reads[i - 1] < reads[i], "BAL StorageReads must be strictly ascending (decoder guarantees this; direct builders must sort).");
        }
    }

    /// <summary>One BAL account's contribution to the read-ordinal space.</summary>
    public readonly struct AccountEntry(ReadOnlyAccountChanges account, int readBase)
    {
        public ReadOnlyAccountChanges Account { get; } = account;

        /// <summary>Global ordinal of this account's first declared read (prefix sum of prior accounts' read counts).</summary>
        public int ReadBase { get; } = readBase;
    }
}
