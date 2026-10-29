// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Serialization.Rlp.Eip7928;

public class BlockAccessListDecoder : RlpDecoder<ReadOnlyBlockAccessList>
{
    public static readonly BlockAccessListDecoder Instance = new();

    private static readonly RlpLimit _accountsLimit = new(Eip7928Constants.MaxAccounts, "", ReadOnlyMemory<char>.Empty);

    public override int GetLength(ReadOnlyBlockAccessList? item, RlpBehaviors rlpBehaviors)
        => Rlp.LengthOfSequence(GetContentLength(item ?? throw new ArgumentNullException(nameof(item)), rlpBehaviors));

    protected override ReadOnlyBlockAccessList DecodeInternal(ref RlpReader ctx, RlpBehaviors rlpBehaviors)
    {
        // Capture the BAL's RLP slice so the wire hash can be cached on the returned instance;
        // BlockValidator would otherwise recompute the same keccak per block.
        int startPosition = ctx.Position;

        ReadOnlyAccountChanges[] accountChanges = ctx.DecodeNonNullArray(AccountChangesDecoder.Instance, limit: _accountsLimit);
        ReadOnlySpan<byte> wireRlp = ctx.Data[startPosition..ctx.Position];

        Address? lastAddress = null;
        int itemCount = 0;
        foreach (ReadOnlyAccountChanges accountChange in accountChanges)
        {
            Address address = accountChange.Address;
            if (lastAddress is not null && address.CompareTo(lastAddress) <= 0)
            {
                ThrowAccountChangesOutOfOrder();
            }
            lastAddress = address;

            itemCount += 1 + accountChange.StorageChanges.Length + accountChange.StorageReads.Length;
        }

        Hash256 wireHash = new(ValueKeccak.Compute(wireRlp));
        return new ReadOnlyBlockAccessList(accountChanges, itemCount, wireHash);
    }

    /// <summary>
    /// One-pass RLP encode of a generated BAL into a freshly allocated byte buffer.
    /// </summary>
    /// <remarks>
    /// The generated list is already in canonical order, so it is written as is. Two passes over the accounts: lengths
    /// first, so each list's prefix is known before its content.
    /// </remarks>
    public static byte[] EncodeToBytes(GeneratedBlockAccessList item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        using ArrayPoolListRef<AccountChangesDecoder.EncodingLengths> accountLengths = new(item.AccountCount, item.AccountCount);
        PrepareGeneratedLengths(item, accountLengths.AsSpan(), rlpBehaviors, out int contentLength);
        byte[] bytes = new byte[Rlp.LengthOfSequence(contentLength)];
        RlpWriter writer = new(bytes);
        EncodeGeneratedPrepared(ref writer, item, accountLengths.AsSpan(), contentLength, rlpBehaviors);
        return bytes;
    }

    /// <summary>
    /// Encodes <paramref name="item"/> into a pool-rented byte span. The caller owns the result and must dispose it.
    /// </summary>
    public static ArrayPoolSpan<byte> EncodeToArrayPoolSpan(
        GeneratedBlockAccessList item,
        RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        using ArrayPoolListRef<AccountChangesDecoder.EncodingLengths> accountLengths = new(item.AccountCount, item.AccountCount);
        PrepareGeneratedLengths(item, accountLengths.AsSpan(), rlpBehaviors, out int contentLength);
        ArrayPoolSpan<byte> bytes = new(Rlp.LengthOfSequence(contentLength));
        try
        {
            RlpWriter writer = new(bytes);
            EncodeGeneratedPrepared(ref writer, item, accountLengths.AsSpan(), contentLength, rlpBehaviors);
            return bytes;
        }
        catch
        {
            bytes.Dispose();
            throw;
        }
    }

    /// <inheritdoc cref="EncodeToBytes(GeneratedBlockAccessList, RlpBehaviors)"/>
    public static byte[] EncodeToBytes(ReadOnlyBlockAccessList item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        ReadOnlySpan<ReadOnlyAccountChanges> accounts = item.AccountChanges.AsSpan();
        using ArrayPoolListRef<AccountChangesDecoder.EncodingLengths> accountLengths = new(accounts.Length, accounts.Length);
        PrepareReadOnlyLengths(accounts, accountLengths.AsSpan(), rlpBehaviors, out int contentLength);
        byte[] bytes = new byte[Rlp.LengthOfSequence(contentLength)];
        RlpWriter writer = new(bytes);
        EncodeReadOnlyPrepared(ref writer, accounts, accountLengths.AsSpan(), contentLength, rlpBehaviors);
        return bytes;
    }

    public override void Encode<TWriter>(ref TWriter writer, ReadOnlyBlockAccessList item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        ArgumentNullException.ThrowIfNull(item);
        ReadOnlySpan<ReadOnlyAccountChanges> accounts = item.AccountChanges.AsSpan();
        using ArrayPoolListRef<AccountChangesDecoder.EncodingLengths> accountLengths = new(accounts.Length, accounts.Length);
        PrepareReadOnlyLengths(accounts, accountLengths.AsSpan(), rlpBehaviors, out int contentLength);
        EncodeReadOnlyPrepared(ref writer, accounts, accountLengths.AsSpan(), contentLength, rlpBehaviors);
    }

    private static int GetContentLength(ReadOnlyBlockAccessList item, RlpBehaviors rlpBehaviors)
    {
        AccountChangesDecoder decoder = AccountChangesDecoder.Instance;
        int contentLength = 0;
        foreach (ReadOnlyAccountChanges a in item.AccountChanges)
        {
            contentLength += decoder.GetLength(a, rlpBehaviors);
        }
        return contentLength;
    }

    private static void PrepareReadOnlyLengths(
        scoped ReadOnlySpan<ReadOnlyAccountChanges> accounts,
        scoped Span<AccountChangesDecoder.EncodingLengths> accountLengths,
        RlpBehaviors rlpBehaviors,
        out int contentLength)
    {
        Debug.Assert(accountLengths.Length >= accounts.Length);

        contentLength = 0;
        for (int i = 0; i < accounts.Length; i++)
        {
            AccountChangesDecoder.EncodingLengths accountLength = AccountChangesDecoder.PrepareEncodingLengths(accounts[i], rlpBehaviors);
            accountLengths[i] = accountLength;
            contentLength += Rlp.LengthOfSequence(accountLength.ContentLength);
        }
    }

    private static void PrepareGeneratedLengths(
        GeneratedBlockAccessList item,
        scoped Span<AccountChangesDecoder.EncodingLengths> accountLengths,
        RlpBehaviors rlpBehaviors,
        out int contentLength)
    {
        Debug.Assert(accountLengths.Length >= item.AccountCount);

        contentLength = 0;
        for (int i = 0; i < item.AccountCount; i++)
        {
            AccountChangesDecoder.EncodingLengths accountLength = PrepareEncodingLengths(item.GetAccount(i), rlpBehaviors);
            accountLengths[i] = accountLength;
            contentLength += Rlp.LengthOfSequence(accountLength.ContentLength);
        }
    }

    private static AccountChangesDecoder.EncodingLengths PrepareEncodingLengths(scoped GeneratedBlockAccessList.AccountChanges account, RlpBehaviors rlpBehaviors)
    {
        int storageChanges = 0;
        for (int slot = 0; slot < account.SlotCount; slot++)
        {
            UInt256 key = account.GetSlotKey(slot);
            storageChanges += Rlp.LengthOfSequence(Rlp.LengthOf(key) + Rlp.LengthOfSequence(ChangesContentLength(account.GetSlotChanges(slot), StorageChangeDecoder.Instance, rlpBehaviors)));
        }
        int storageReads = 0;
        foreach (UInt256 read in account.StorageReads) storageReads += Rlp.LengthOf(read);
        int balance = ChangesContentLength(account.BalanceChanges, BalanceChangeDecoder.Instance, rlpBehaviors);
        int nonce = ChangesContentLength(account.NonceChanges, NonceChangeDecoder.Instance, rlpBehaviors);
        int code = ChangesContentLength(account.CodeChanges, CodeChangeDecoder.Instance, rlpBehaviors);
        int contentLength = Rlp.LengthOfAddressRlp
            + Rlp.LengthOfSequence(storageChanges)
            + Rlp.LengthOfSequence(storageReads)
            + Rlp.LengthOfSequence(balance)
            + Rlp.LengthOfSequence(nonce)
            + Rlp.LengthOfSequence(code);
        return new AccountChangesDecoder.EncodingLengths(contentLength, storageChanges, storageReads, balance, nonce, code);
    }

    private static void EncodeReadOnlyPrepared<TWriter>(
        ref TWriter writer,
        scoped ReadOnlySpan<ReadOnlyAccountChanges> accounts,
        scoped ReadOnlySpan<AccountChangesDecoder.EncodingLengths> accountLengths,
        int contentLength,
        RlpBehaviors rlpBehaviors)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        Debug.Assert(accountLengths.Length >= accounts.Length);

        writer.StartSequence(contentLength);
        AccountChangesDecoder accountChangesDecoder = AccountChangesDecoder.Instance;
        for (int i = 0; i < accounts.Length; i++)
        {
            accountChangesDecoder.EncodePrepared(ref writer, accounts[i], in accountLengths[i], rlpBehaviors);
        }
    }

    private static void EncodeGeneratedPrepared(
        ref RlpWriter writer,
        GeneratedBlockAccessList item,
        scoped ReadOnlySpan<AccountChangesDecoder.EncodingLengths> accountLengths,
        int contentLength,
        RlpBehaviors rlpBehaviors)
    {
        Debug.Assert(accountLengths.Length >= item.AccountCount);

        writer.StartSequence(contentLength);
        for (int i = 0; i < item.AccountCount; i++)
        {
            EncodeAccount(ref writer, item.GetAccount(i), in accountLengths[i], rlpBehaviors);
        }
    }

    private static void EncodeAccount(ref RlpWriter writer, scoped GeneratedBlockAccessList.AccountChanges account, in AccountChangesDecoder.EncodingLengths lengths, RlpBehaviors rlpBehaviors)
    {
        writer.StartSequence(lengths.ContentLength);
        writer.Encode(account.Address);
        writer.StartSequence(lengths.StorageChangesContentLength);
        for (int slot = 0; slot < account.SlotCount; slot++)
        {
            ReadOnlySpan<StorageChange> changes = account.GetSlotChanges(slot);
            UInt256 key = account.GetSlotKey(slot);
            int changesLength = ChangesContentLength(changes, StorageChangeDecoder.Instance, rlpBehaviors);
            writer.StartSequence(Rlp.LengthOf(key) + Rlp.LengthOfSequence(changesLength));
            writer.Encode(key);
            EncodeChanges(ref writer, changes, changesLength, StorageChangeDecoder.Instance, rlpBehaviors);
        }
        writer.StartSequence(lengths.StorageReadsContentLength);
        foreach (UInt256 read in account.StorageReads) writer.Encode(read);
        EncodeChanges(ref writer, account.BalanceChanges, lengths.BalanceContentLength, BalanceChangeDecoder.Instance, rlpBehaviors);
        EncodeChanges(ref writer, account.NonceChanges, lengths.NonceContentLength, NonceChangeDecoder.Instance, rlpBehaviors);
        EncodeChanges(ref writer, account.CodeChanges, lengths.CodeContentLength, CodeChangeDecoder.Instance, rlpBehaviors);
    }

    private static int ChangesContentLength<T>(scoped ReadOnlySpan<T> items, IndexedChangeDecoder<T> decoder, RlpBehaviors rlpBehaviors) where T : struct, IIndexedChange
    {
        int length = 0;
        foreach (T item in items) length += decoder.GetLength(item, rlpBehaviors);
        return length;
    }

    private static void EncodeChanges<T>(ref RlpWriter writer, scoped ReadOnlySpan<T> items, int contentLength, IndexedChangeDecoder<T> decoder, RlpBehaviors rlpBehaviors) where T : struct, IIndexedChange
    {
        writer.StartSequence(contentLength);
        foreach (T item in items) decoder.Encode(ref writer, item, rlpBehaviors);
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowAccountChangesOutOfOrder() =>
        throw new RlpException("Account changes were in incorrect order.");

}
