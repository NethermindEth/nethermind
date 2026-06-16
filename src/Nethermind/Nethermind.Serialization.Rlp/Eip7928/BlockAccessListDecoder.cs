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

    public override int GetLength(ReadOnlyBlockAccessList item, RlpBehaviors rlpBehaviors)
        => Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));

    public int GetLength(GeneratedBlockAccessList item, RlpBehaviors rlpBehaviors)
        => Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));

    protected override ReadOnlyBlockAccessList DecodeInternal(ref Rlp.ValueDecoderContext ctx, RlpBehaviors rlpBehaviors)
    {
        // Capture the BAL's RLP slice so the wire hash can be cached on the returned instance;
        // BlockValidator would otherwise recompute the same keccak per block.
        int startPosition = ctx.Position;

        ReadOnlyAccountChanges[] accountChanges = ctx.DecodeArray(AccountChangesDecoder.Instance, limit: _accountsLimit);
        ReadOnlySpan<byte> wireRlp = ctx.Data[startPosition..ctx.Position];

        Address? lastAddress = null;
        int itemCount = 0;
        foreach (ReadOnlyAccountChanges a in accountChanges)
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
        return new ReadOnlyBlockAccessList(accountChanges, itemCount, wireHash);
    }

    /// <summary>
    /// One-pass RLP encode of a generated BAL into a freshly allocated byte buffer.
    /// </summary>
    public static byte[] EncodeToBytes(GeneratedBlockAccessList item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        using ArrayPoolListRef<GeneratedAccountChanges> sortedAccounts = item.GetSortedAccountChanges();
        using ArrayPoolListRef<AccountChangesDecoder.EncodingLengths> accountLengths = new(sortedAccounts.Count, sortedAccounts.Count);
        PrepareGeneratedLengths(sortedAccounts.AsSpan(), accountLengths.AsSpan(), rlpBehaviors, out int contentLength);
        RlpStream stream = new(Rlp.LengthOfSequence(contentLength));
        EncodeGeneratedPrepared(stream, sortedAccounts.AsSpan(), accountLengths.AsSpan(), contentLength, rlpBehaviors);
        return stream.Data.ToArray();
    }

    /// <inheritdoc cref="EncodeToBytes(GeneratedBlockAccessList, RlpBehaviors)"/>
    public static byte[] EncodeToBytes(ReadOnlyBlockAccessList item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        ReadOnlySpan<ReadOnlyAccountChanges> accounts = item.AccountChanges.AsSpan();
        using ArrayPoolListRef<AccountChangesDecoder.EncodingLengths> accountLengths = new(accounts.Length, accounts.Length);
        PrepareReadOnlyLengths(accounts, accountLengths.AsSpan(), rlpBehaviors, out int contentLength);
        RlpStream stream = new(Rlp.LengthOfSequence(contentLength));
        EncodeReadOnlyPrepared(stream, accounts, accountLengths.AsSpan(), contentLength, rlpBehaviors);
        return stream.Data.ToArray();
    }

    public override void Encode(RlpStream stream, ReadOnlyBlockAccessList item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        ReadOnlySpan<ReadOnlyAccountChanges> accounts = item.AccountChanges.AsSpan();
        using ArrayPoolListRef<AccountChangesDecoder.EncodingLengths> accountLengths = new(accounts.Length, accounts.Length);
        PrepareReadOnlyLengths(accounts, accountLengths.AsSpan(), rlpBehaviors, out int contentLength);
        EncodeReadOnlyPrepared(stream, accounts, accountLengths.AsSpan(), contentLength, rlpBehaviors);
    }

    public void Encode(RlpStream stream, GeneratedBlockAccessList item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        using ArrayPoolListRef<GeneratedAccountChanges> sortedAccounts = item.GetSortedAccountChanges();
        using ArrayPoolListRef<AccountChangesDecoder.EncodingLengths> accountLengths = new(sortedAccounts.Count, sortedAccounts.Count);
        PrepareGeneratedLengths(sortedAccounts.AsSpan(), accountLengths.AsSpan(), rlpBehaviors, out int contentLength);
        EncodeGeneratedPrepared(stream, sortedAccounts.AsSpan(), accountLengths.AsSpan(), contentLength, rlpBehaviors);
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
        ReadOnlySpan<ReadOnlyAccountChanges> accounts,
        Span<AccountChangesDecoder.EncodingLengths> accountLengths,
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
        ReadOnlySpan<GeneratedAccountChanges> sortedAccounts,
        Span<AccountChangesDecoder.EncodingLengths> accountLengths,
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
        RlpStream stream,
        ReadOnlySpan<ReadOnlyAccountChanges> accounts,
        ReadOnlySpan<AccountChangesDecoder.EncodingLengths> accountLengths,
        int contentLength,
        RlpBehaviors rlpBehaviors)
    {
        Debug.Assert(accountLengths.Length >= accounts.Length);

        stream.StartSequence(contentLength);
        AccountChangesDecoder accountChangesDecoder = AccountChangesDecoder.Instance;
        for (int i = 0; i < accounts.Length; i++)
        {
            accountChangesDecoder.EncodePrepared(stream, accounts[i], in accountLengths[i], rlpBehaviors);
        }
    }

    private static void EncodeGeneratedPrepared(
        RlpStream stream,
        ReadOnlySpan<GeneratedAccountChanges> sortedAccounts,
        ReadOnlySpan<AccountChangesDecoder.EncodingLengths> accountLengths,
        int contentLength,
        RlpBehaviors rlpBehaviors)
    {
        Debug.Assert(accountLengths.Length >= sortedAccounts.Length);

        stream.StartSequence(contentLength);
        AccountChangesDecoder accountChangesDecoder = AccountChangesDecoder.Instance;
        for (int i = 0; i < sortedAccounts.Length; i++)
        {
            accountChangesDecoder.EncodePrepared(stream, sortedAccounts[i], in accountLengths[i], rlpBehaviors);
        }
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowAccountChangesOutOfOrder() =>
        throw new RlpException("Account changes were in incorrect order.");
}
