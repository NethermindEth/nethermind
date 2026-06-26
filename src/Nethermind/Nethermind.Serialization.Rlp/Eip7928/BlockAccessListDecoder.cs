// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

namespace Nethermind.Serialization.Rlp.Eip7928;

public class BlockAccessListDecoder : RlpDecoder<ReadOnlyBlockAccessList>
{
    public static readonly BlockAccessListDecoder Instance = new();

    private static readonly RlpLimit _accountsLimit = new(Eip7928Constants.MaxAccounts, "", ReadOnlyMemory<char>.Empty);

    public override int GetLength(ReadOnlyBlockAccessList? item, RlpBehaviors rlpBehaviors)
        => Rlp.LengthOfSequence(GetContentLength(item ?? throw new ArgumentNullException(nameof(item)), rlpBehaviors));

    public int GetLength(GeneratedBlockAccessList item, RlpBehaviors rlpBehaviors)
        => Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));

    protected override ReadOnlyBlockAccessList DecodeInternal(ref RlpReader ctx, RlpBehaviors rlpBehaviors)
    {
        // Capture the BAL's RLP slice so the wire hash can be cached on the returned instance;
        // BlockValidator would otherwise recompute the same keccak per block.
        int startPosition = ctx.Position;

        ReadOnlyAccountChanges[] accountChanges = ctx.DecodeArray(AccountChangesDecoder.Instance, limit: _accountsLimit);
        ReadOnlySpan<byte> wireRlp = ctx.Data[startPosition..ctx.Position];

        Address? lastAddress = null;
        int itemCount = 0;
        foreach (ReadOnlyAccountChanges? a in accountChanges)
        {
            Address address = a!.Address;
            if (lastAddress is not null && address.CompareTo(lastAddress) <= 0)
            {
                ThrowAccountChangesOutOfOrder();
            }
            lastAddress = address;

            itemCount += 1 + a.StorageChanges.Length + a.StorageReads.Length;
        }

        Hash256 wireHash = new(ValueKeccak.Compute(wireRlp));
        return new ReadOnlyBlockAccessList(accountChanges!, itemCount, wireHash);
    }

    /// <summary>
    /// One-pass RLP encode of a generated BAL into a freshly allocated byte buffer.
    /// </summary>
    public static byte[] EncodeToBytes(GeneratedBlockAccessList item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        using ArrayPoolListRef<GeneratedAccountChanges> sortedAccounts = item.GetSortedAccountChanges();
        using ArrayPoolListRef<AccountChangesDecoder.EncodingLengths> accountLengths = new(sortedAccounts.Count, sortedAccounts.Count);
        PrepareGeneratedLengths(sortedAccounts.AsSpan(), accountLengths.AsSpan(), rlpBehaviors, out int contentLength);
        byte[] bytes = new byte[Rlp.LengthOfSequence(contentLength)];
        RlpWriter writer = new(bytes);
        EncodeGeneratedPrepared(ref writer, sortedAccounts.AsSpan(), accountLengths.AsSpan(), contentLength, rlpBehaviors);
        return bytes;
    }

    /// <summary>
    /// Encodes <paramref name="item"/> into a pool-rented byte span. The caller owns the result and must dispose it.
    /// </summary>
    public static ArrayPoolSpan<byte> EncodeToArrayPoolSpan(
        GeneratedBlockAccessList item,
        RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        using ArrayPoolListRef<GeneratedAccountChanges> sortedAccounts = item.GetSortedAccountChanges();
        using ArrayPoolListRef<AccountChangesDecoder.EncodingLengths> accountLengths = new(sortedAccounts.Count, sortedAccounts.Count);
        PrepareGeneratedLengths(sortedAccounts.AsSpan(), accountLengths.AsSpan(), rlpBehaviors, out int contentLength);
        ArrayPoolSpan<byte> bytes = new(Rlp.LengthOfSequence(contentLength));
        try
        {
            RlpWriter writer = new(bytes);
            EncodeGeneratedPrepared(ref writer, sortedAccounts.AsSpan(), accountLengths.AsSpan(), contentLength, rlpBehaviors);
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

    public void Encode<TWriter>(ref TWriter writer, GeneratedBlockAccessList item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        using ArrayPoolListRef<GeneratedAccountChanges> sortedAccounts = item.GetSortedAccountChanges();
        using ArrayPoolListRef<AccountChangesDecoder.EncodingLengths> accountLengths = new(sortedAccounts.Count, sortedAccounts.Count);
        PrepareGeneratedLengths(sortedAccounts.AsSpan(), accountLengths.AsSpan(), rlpBehaviors, out int contentLength);
        EncodeGeneratedPrepared(ref writer, sortedAccounts.AsSpan(), accountLengths.AsSpan(), contentLength, rlpBehaviors);
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

    private static int GetContentLength(GeneratedBlockAccessList item, RlpBehaviors rlpBehaviors)
    {
        AccountChangesDecoder decoder = AccountChangesDecoder.Instance;
        int contentLength = 0;
        foreach (GeneratedAccountChanges a in item.AccountChanges)
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
        scoped ReadOnlySpan<GeneratedAccountChanges> sortedAccounts,
        scoped Span<AccountChangesDecoder.EncodingLengths> accountLengths,
        RlpBehaviors rlpBehaviors,
        out int contentLength)
    {
        Debug.Assert(accountLengths.Length >= sortedAccounts.Length);

        contentLength = 0;
        for (int i = 0; i < sortedAccounts.Length; i++)
        {
            AccountChangesDecoder.EncodingLengths accountLength = AccountChangesDecoder.PrepareEncodingLengths(sortedAccounts[i], rlpBehaviors);
            accountLengths[i] = accountLength;
            contentLength += Rlp.LengthOfSequence(accountLength.ContentLength);
        }
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

    private static void EncodeGeneratedPrepared<TWriter>(
        ref TWriter writer,
        scoped ReadOnlySpan<GeneratedAccountChanges> sortedAccounts,
        scoped ReadOnlySpan<AccountChangesDecoder.EncodingLengths> accountLengths,
        int contentLength,
        RlpBehaviors rlpBehaviors)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        Debug.Assert(accountLengths.Length >= sortedAccounts.Length);

        writer.StartSequence(contentLength);
        AccountChangesDecoder accountChangesDecoder = AccountChangesDecoder.Instance;
        for (int i = 0; i < sortedAccounts.Length; i++)
        {
            accountChangesDecoder.EncodePrepared(ref writer, sortedAccounts[i], in accountLengths[i], rlpBehaviors);
        }
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowAccountChangesOutOfOrder() =>
        throw new RlpException("Account changes were in incorrect order.");

}
