// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.FluentRlp;

namespace Nethermind.Serialization.Rlp.Eip7928;

public class BlockAccessListDecoder : RlpDecoder<ReadOnlyBlockAccessList>
{
    public static readonly BlockAccessListDecoder Instance = new();

    private static readonly RlpLimit _accountsLimit = new(Eip7928Constants.MaxAccounts, "", ReadOnlyMemory<char>.Empty);

    public override int GetLength(ReadOnlyBlockAccessList? item, RlpBehaviors rlpBehaviors)
        => Rlp.LengthOfSequence(GetContentLength(item ?? throw new ArgumentNullException(nameof(item)), rlpBehaviors));

    public int GetLength(GeneratedBlockAccessList item, RlpBehaviors rlpBehaviors)
        => Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));

    protected override ReadOnlyBlockAccessList DecodeInternal(ref RlpReader reader, RlpBehaviors rlpBehaviors)
        => (rlpBehaviors & RlpBehaviors.TrustedInput) != 0 ? Decode<OffFlag>(ref reader) : Decode<OnFlag>(ref reader);

    private static ReadOnlyBlockAccessList Decode<TCanonicalChecks>(ref RlpReader reader) where TCanonicalChecks : struct, IFlag
    {
        // Capture the BAL's RLP slice so the wire hash can be cached on the returned instance;
        // BlockValidator would otherwise recompute the same keccak per block.
        RlpReader start = reader;
        ReadOnlySpan<byte> wireRlp = start.ReadEncodedItem<TCanonicalChecks>();

        ReadOnlyAccountChanges[] accountChanges = DecodeAccountChanges<TCanonicalChecks>(ref reader);

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

    private static ReadOnlyAccountChanges[] DecodeAccountChanges<TCanonicalChecks>(ref RlpReader reader) where TCanonicalChecks : struct, IFlag
    {
        RlpBehaviors itemBehaviors = TCanonicalChecks.IsActive ? RlpBehaviors.None : RlpBehaviors.TrustedInput;
        RlpReader items = reader.EnterSequence<TCanonicalChecks>(out int itemsLength);
        int count = items.CountItems<TCanonicalChecks>(_accountsLimit);
        ReadOnlyAccountChanges[] result = count == 0 ? [] : new ReadOnlyAccountChanges[count];
        // A span store has no covariance check, unlike an array store of a reference type.
        Span<ReadOnlyAccountChanges> span = result;
        for (int i = 0; i < span.Length; i++)
        {
            span[i] = AccountChangesDecoder.Decode<TCanonicalChecks>(ref items, itemBehaviors);
        }

        reader.ExitSequence(in items, itemsLength);
        return result;
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
        RlpWriter writer = RlpWriter.ContentWriter(bytes);
        EncodeGeneratedPrepared(ref writer, sortedAccounts.AsSpan(), accountLengths.AsSpan(), contentLength, rlpBehaviors);
        Rlp.CheckLength(in writer, bytes.Length);
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
            RlpWriter writer = RlpWriter.ContentWriter(bytes);
            EncodeGeneratedPrepared(ref writer, sortedAccounts.AsSpan(), accountLengths.AsSpan(), contentLength, rlpBehaviors);
            Rlp.CheckLength(in writer, bytes.Length);
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
        RlpWriter writer = RlpWriter.ContentWriter(bytes);
        EncodeReadOnlyPrepared(ref writer, accounts, accountLengths.AsSpan(), contentLength, rlpBehaviors);
        Rlp.CheckLength(in writer, bytes.Length);
        return bytes;
    }

    public override void Encode(ref RlpWriter writer, ReadOnlyBlockAccessList item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        ArgumentNullException.ThrowIfNull(item);
        ReadOnlySpan<ReadOnlyAccountChanges> accounts = item.AccountChanges.AsSpan();
        using ArrayPoolListRef<AccountChangesDecoder.EncodingLengths> accountLengths = new(accounts.Length, accounts.Length);
        PrepareReadOnlyLengths(accounts, accountLengths.AsSpan(), rlpBehaviors, out int contentLength);
        EncodeReadOnlyPrepared(ref writer, accounts, accountLengths.AsSpan(), contentLength, rlpBehaviors);
    }

    public void Encode(ref RlpWriter writer, GeneratedBlockAccessList item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
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

    private static void EncodeReadOnlyPrepared(
        ref RlpWriter writer,
        scoped ReadOnlySpan<ReadOnlyAccountChanges> accounts,
        scoped ReadOnlySpan<AccountChangesDecoder.EncodingLengths> accountLengths,
        int contentLength,
        RlpBehaviors rlpBehaviors)
    {
        Debug.Assert(accountLengths.Length >= accounts.Length);

        RlpSequence sequence = writer.StartSequence(contentLength);
        AccountChangesDecoder accountChangesDecoder = AccountChangesDecoder.Instance;
        for (int i = 0; i < accounts.Length; i++)
        {
            accountChangesDecoder.EncodePrepared(ref writer, accounts[i], in accountLengths[i], rlpBehaviors);
        }

        writer.EndSequence(sequence);
    }

    private static void EncodeGeneratedPrepared(
        ref RlpWriter writer,
        scoped ReadOnlySpan<GeneratedAccountChanges> sortedAccounts,
        scoped ReadOnlySpan<AccountChangesDecoder.EncodingLengths> accountLengths,
        int contentLength,
        RlpBehaviors rlpBehaviors)
    {
        Debug.Assert(accountLengths.Length >= sortedAccounts.Length);

        RlpSequence sequence = writer.StartSequence(contentLength);
        AccountChangesDecoder accountChangesDecoder = AccountChangesDecoder.Instance;
        for (int i = 0; i < sortedAccounts.Length; i++)
        {
            accountChangesDecoder.EncodePrepared(ref writer, sortedAccounts[i], in accountLengths[i], rlpBehaviors);
        }

        writer.EndSequence(sequence);
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowAccountChangesOutOfOrder() =>
        throw new RlpReaderException("Account changes were in incorrect order.");

}
