// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;
using Paprika;
using Paprika.Data;
using Paprika.Store;

namespace Nethermind.State.Flat.Persistence.PaprikaFlat;

/// <summary>The range reads and range deletes of <see cref="Db.FlatLayout.PaprikaFlat"/>, with the other flat layouts' bounds.</summary>
/// <remarks>
/// <para>
/// Accounts are iterated over <c>[start, end)</c> and slots over <c>[start, end]</c>, comparing an account on its first
/// <see cref="PaprikaFlatKeys.AccountKeyLength"/> bytes; range deletes are inclusive at both ends.
/// </para>
/// <para>
/// A delete reads the keys it removes from <c>preBatch</c>, a reader of the root the write batch was begun on, so writes
/// made to the batch afterwards survive it. A contract's whole storage is dropped with one prefix delete;
/// <see cref="IBatch.Destroy"/> is never used, as it would map every destroyed account to one contract id.
/// </para>
/// <para>
/// A range delete splits the range into maximal aligned nibble-prefix blocks. A block the range covers is dropped with
/// one prefix delete, which releases its pages instead of copying every page a key-by-key delete would touch; one with no
/// key in <c>preBatch</c> is skipped, so it costs only the lookup. Only the at most two blocks of the last nibble that the
/// range covers in part are deleted key by key. The drops run in a first pass, before any write of the batch; the
/// key-by-key deletes in a second.
/// </para>
/// </remarks>
internal static class PaprikaFlatRanges
{
    /// <summary>The nibbles of a state key the state root page consumes.</summary>
    /// <remarks>
    /// An account block shorter than this is split into blocks of this length, so that no prefix delete reaches the state
    /// root page's own map, which holds the markers.
    /// </remarks>
    private const int StateRootNibbles = 2;

    /// <summary>Iterates the accounts whose path is in <c>[<paramref name="start"/>, <paramref name="end"/>)</c>.</summary>
    /// <returns>An iterator whose key is the account key zero-padded to 32 bytes and whose value is the stored Slim RLP.</returns>
    public static IPersistence.IFlatIterator CreateAccountIterator(IVisitableReadOnlyBatch reader, in ValueHash256 start, in ValueHash256 end)
    {
        ValueHash256 endAccount = PaprikaFlatKeys.StorageAccount(end.Bytes);
        if (endAccount == ValueKeccak.Zero) return new AccountIterator(RangeIterator.Empty);

        // Paprika's bounds are inclusive: the last account before the end is the end's account key minus one.
        ValueHash256 lastAccount = endAccount.DecrementPath();
        return new AccountIterator(reader.IterateState(start.Bytes[..PaprikaFlatKeys.AccountKeyLength], lastAccount.Bytes[..PaprikaFlatKeys.AccountKeyLength]));
    }

    /// <summary>Iterates the slots in <c>[<paramref name="start"/>, <paramref name="end"/>]</c> of the storage of <paramref name="account"/>.</summary>
    /// <returns>An iterator whose key is the slot hash and whose value is the stripped big-endian slot value.</returns>
    public static IPersistence.IFlatIterator CreateStorageIterator(IVisitableReadOnlyBatch reader, in ValueHash256 account, in ValueHash256 start, in ValueHash256 end)
    {
        ValueHash256 storageAccount = PaprikaFlatKeys.StorageAccount(account.Bytes);
        return new StorageIterator(reader.IterateStorage(storageAccount.Bytes, start.Bytes, end.Bytes));
    }

    /// <summary>Deletes the accounts whose path is in <c>[<paramref name="from"/>, <paramref name="to"/>]</c> in <paramref name="preBatch"/>.</summary>
    /// <param name="structural">
    /// <c>true</c> for the first pass, which drops the blocks the range covers; <c>false</c> for the second, which deletes
    /// the accounts of the blocks of the last nibble it covers in part.
    /// </param>
    public static void DeleteAccountRange(IVisitableReadOnlyBatch preBatch, IBatch batch, in ValueHash256 from, in ValueHash256 to, bool structural) =>
        DeleteRange(preBatch, batch, ReadOnlySpan<byte>.Empty, from.Bytes[..PaprikaFlatKeys.AccountKeyLength], to.Bytes[..PaprikaFlatKeys.AccountKeyLength], 0, structural);

    /// <summary>Drops the whole storage of <paramref name="storageAccount"/> if it has a slot in <paramref name="preBatch"/>.</summary>
    /// <param name="storageAccount">The 32-byte contract key, see <see cref="PaprikaFlatKeys.StorageAccount"/>.</param>
    public static void DeleteStorage(IVisitableReadOnlyBatch preBatch, IBatch batch, ReadOnlySpan<byte> storageAccount)
    {
        ValueHash256 firstSlot = ValueKeccak.Zero;
        ValueHash256 lastSlot = ValueKeccak.MaxValue;
        using (RangeIterator slots = preBatch.IterateStorage(storageAccount, firstSlot.Bytes, lastSlot.Bytes))
        {
            if (!slots.MoveNext()) return;
        }

        batch.DeleteByPrefix(PaprikaFlatKeys.Storage(storageAccount));
    }

    /// <summary>
    /// Deletes the slots in <c>[<paramref name="from"/>, <paramref name="to"/>]</c> of the storage of
    /// <paramref name="account"/> in <paramref name="preBatch"/>; the whole range drops the storage with one prefix delete.
    /// </summary>
    /// <param name="structural">
    /// <c>true</c> for the first pass, which drops the blocks the range covers; <c>false</c> for the second, which deletes
    /// the slots of the blocks of the last nibble it covers in part.
    /// </param>
    public static void DeleteStorageRange(IVisitableReadOnlyBatch preBatch, IBatch batch, in ValueHash256 account, in ValueHash256 from, in ValueHash256 to, bool structural)
    {
        ValueHash256 storageAccount = PaprikaFlatKeys.StorageAccount(account.Bytes);
        DeleteRange(preBatch, batch, storageAccount.Bytes, from.Bytes, to.Bytes, 0, structural);
    }

    /// <summary>Drops the whole storage of <paramref name="account"/>; the account itself is kept.</summary>
    public static void SelfDestruct(IVisitableReadOnlyBatch preBatch, IBatch batch, in ValueHash256 account)
    {
        ValueHash256 storageAccount = PaprikaFlatKeys.StorageAccount(account.Bytes);
        DeleteStorage(preBatch, batch, storageAccount.Bytes);
    }

    /// <summary>
    /// Drops the whole storage of every contract whose path is in <c>[<paramref name="from"/>, <paramref name="to"/>]</c> in
    /// <paramref name="preBatch"/>, whether or not its account exists.
    /// </summary>
    /// <remarks>
    /// It enumerates the contracts rather than the accounts, so storage left without an account is deleted too, as the
    /// other flat layouts' key scan deletes it.
    /// </remarks>
    public static void SelfDestructRange(IVisitableReadOnlyBatch preBatch, IBatch batch, in ValueHash256 from, in ValueHash256 to)
    {
        ValueHash256 first = PaprikaFlatKeys.StorageAccount(from.Bytes);
        ValueHash256 last = PaprikaFlatKeys.StorageAccount(to.Bytes);
        last.BytesAsSpan[PaprikaFlatKeys.AccountKeyLength..].Fill(0xFF);

        using RangeIterator contracts = preBatch.IterateContracts(first.Bytes, last.Bytes);
        while (contracts.MoveNext()) DeleteStorage(preBatch, batch, contracts.CurrentKey);
    }

    /// <summary>
    /// Deletes the keys in <paramref name="preBatch"/> of <c>[<paramref name="lo"/>, <paramref name="hi"/>]</c>, whose first
    /// <paramref name="depth"/> nibbles are equal, from the state, or from the storage of <paramref name="storageAccount"/>.
    /// </summary>
    /// <param name="storageAccount">The 32-byte contract key, or empty for the state.</param>
    /// <param name="structural">Whether to drop the blocks the range covers, or to delete the keys of the blocks of the last nibble it covers in part.</param>
    private static void DeleteRange(IVisitableReadOnlyBatch preBatch, IBatch batch, ReadOnlySpan<byte> storageAccount,
        ReadOnlySpan<byte> lo, ReadOnlySpan<byte> hi, int depth, bool structural)
    {
        int lastNibble = lo.Length * NibblePath.NibblePerByte - 1;
        while (depth < lastNibble && Nibble(lo, depth) == Nibble(hi, depth)) depth++;

        bool covered = HasSuffix(lo, depth, 0x00) && HasSuffix(hi, depth, 0xFF);
        if (!covered && depth == lastNibble)
        {
            if (!structural) DeleteKeys(preBatch, batch, storageAccount, lo, hi);
            return;
        }

        if ((structural || !covered) && !HasKey(preBatch, storageAccount, lo, hi)) return;

        if (covered && (depth >= StateRootNibbles || !storageAccount.IsEmpty))
        {
            if (structural)
            {
                NibblePath prefix = NibblePath.FromKey(lo, 0, depth);
                batch.DeleteByPrefix(storageAccount.IsEmpty ? Key.Account(prefix) : Key.StorageCell(NibblePath.FromKey(storageAccount), prefix));
            }

            return;
        }

        Span<byte> childLo = stackalloc byte[lo.Length];
        Span<byte> childHi = stackalloc byte[hi.Length];
        int first = Nibble(lo, depth);
        int last = Nibble(hi, depth);
        for (int nibble = first; nibble <= last; nibble++)
        {
            lo.CopyTo(childLo);
            hi.CopyTo(childHi);
            if (nibble > first) SetNibble(childLo, depth, nibble, 0x00);
            if (nibble < last) SetNibble(childHi, depth, nibble, 0xFF);
            DeleteRange(preBatch, batch, storageAccount, childLo, childHi, depth + 1, structural);
        }
    }

    private static bool HasKey(IVisitableReadOnlyBatch preBatch, ReadOnlySpan<byte> storageAccount, ReadOnlySpan<byte> lo, ReadOnlySpan<byte> hi)
    {
        using RangeIterator keys = Iterate(preBatch, storageAccount, lo, hi);
        return keys.MoveNext();
    }

    private static void DeleteKeys(IVisitableReadOnlyBatch preBatch, IBatch batch, ReadOnlySpan<byte> storageAccount, ReadOnlySpan<byte> lo, ReadOnlySpan<byte> hi)
    {
        using RangeIterator keys = Iterate(preBatch, storageAccount, lo, hi);
        while (keys.MoveNext())
        {
            batch.SetRaw(storageAccount.IsEmpty ? PaprikaFlatKeys.Account(keys.CurrentKey) : PaprikaFlatKeys.Slot(storageAccount, keys.CurrentKey), ReadOnlySpan<byte>.Empty);
        }
    }

    private static RangeIterator Iterate(IVisitableReadOnlyBatch preBatch, ReadOnlySpan<byte> storageAccount, ReadOnlySpan<byte> lo, ReadOnlySpan<byte> hi) =>
        storageAccount.IsEmpty ? preBatch.IterateState(lo, hi) : preBatch.IterateStorage(storageAccount, lo, hi);

    private static int Nibble(ReadOnlySpan<byte> key, int index) =>
        index % NibblePath.NibblePerByte == 0 ? key[index / NibblePath.NibblePerByte] >> NibblePath.NibbleShift : key[index / NibblePath.NibblePerByte] & NibblePath.NibbleMask;

    /// <summary>Whether every nibble of <paramref name="key"/> from <paramref name="depth"/> on is the nibble of <paramref name="fill"/>.</summary>
    private static bool HasSuffix(ReadOnlySpan<byte> key, int depth, byte fill) =>
        (depth % NibblePath.NibblePerByte == 0 || Nibble(key, depth) == (fill & NibblePath.NibbleMask)) &&
        !key[((depth + 1) / NibblePath.NibblePerByte)..].ContainsAnyExcept(fill);

    /// <summary>Sets the nibble of <paramref name="key"/> at <paramref name="depth"/> to <paramref name="nibble"/>, and every nibble after it to the nibble of <paramref name="fill"/>.</summary>
    private static void SetNibble(Span<byte> key, int depth, int nibble, byte fill)
    {
        int index = depth / NibblePath.NibblePerByte;
        key[index] = depth % NibblePath.NibblePerByte == 0
            ? (byte)(nibble << NibblePath.NibbleShift | fill & NibblePath.NibbleMask)
            : (byte)(key[index] & ~NibblePath.NibbleMask | nibble);
        key[(index + 1)..].Fill(fill);
    }

    /// <summary>The stripped big-endian value of a stored slot, which is RLP-wrapped as in the other flat layouts.</summary>
    /// <exception cref="InvalidDataException">The value is longer than 32 bytes.</exception>
    public static ReadOnlySpan<byte> DecodeSlot(ReadOnlySpan<byte> stored)
    {
        ReadOnlySpan<byte> value = new RlpReader(stored).DecodeByteArraySpan();
        if (value.Length > BaseFlatPersistence.StorageValueSize) ThrowSlotValueTooLong(value.Length);
        return value;
    }

    [DoesNotReturn]
    private static void ThrowSlotValueTooLong(int length) =>
        throw new InvalidDataException($"PaprikaFlat storage slot value is {length} bytes, exceeding the {BaseFlatPersistence.StorageValueSize}-byte maximum.");

    private sealed class AccountIterator(RangeIterator accounts) : IPersistence.IFlatIterator
    {
        public bool MoveNext() => accounts.MoveNext();

        public ValueHash256 CurrentKey => PaprikaFlatKeys.StorageAccount(accounts.CurrentKey);

        public ReadOnlySpan<byte> CurrentValue => accounts.CurrentValue;

        public void Dispose() => accounts.Dispose();
    }

    private sealed class StorageIterator(RangeIterator slots) : IPersistence.IFlatIterator
    {
        /// <remarks>Fails on a slot value over 32 bytes here, as the other flat layouts do, rather than on its read.</remarks>
        public bool MoveNext()
        {
            if (!slots.MoveNext()) return false;
            DecodeSlot(slots.CurrentValue);
            return true;
        }

        public ValueHash256 CurrentKey => new(slots.CurrentKey);

        public ReadOnlySpan<byte> CurrentValue => DecodeSlot(slots.CurrentValue);

        public void Dispose() => slots.Dispose();
    }
}
