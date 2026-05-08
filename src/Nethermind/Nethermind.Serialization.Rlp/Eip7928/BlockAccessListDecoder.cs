// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;

namespace Nethermind.Serialization.Rlp.Eip7928;

public class BlockAccessListDecoder : IRlpValueDecoder<BlockAccessList>, IRlpStreamEncoder<BlockAccessList>
{
    private static BlockAccessListDecoder? _instance = null;
    public static BlockAccessListDecoder Instance => _instance ??= new();

    private static readonly RlpLimit _accountsLimit = new(Eip7928Constants.MaxAccounts, "", ReadOnlyMemory<char>.Empty);

    public int GetLength(BlockAccessList item, RlpBehaviors rlpBehaviors)
        => Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));

    public BlockAccessList Decode(ref Rlp.ValueDecoderContext ctx, RlpBehaviors rlpBehaviors)
    {
        AccountChanges[] accountChanges = ctx.DecodeArray(AccountChangesDecoder.Instance, true, default, _accountsLimit);

        Address? lastAddress = null;
        long itemCount = 0;
        foreach (AccountChanges a in accountChanges)
        {
            // EIP-7928 AccountChanges is a 6-field sequence; an empty inner
            // list (RLP 0xc0) is rejected by DecodeArray as defaultElement -> null.
            if (a is null)
            {
                ThrowEmptyAccountChanges();
            }

            Address address = a.Address;
            if (lastAddress is not null && address.CompareTo(lastAddress) <= 0)
            {
                ThrowAccountChangesOutOfOrder();
            }
            lastAddress = address;

            itemCount += 1L + a.StorageChanges.Count + a.StorageReads.Count;
        }

        return BlockAccessList.FromSortedAccountChanges(accountChanges, itemCount);
    }

    public byte[] EncodeToBytes(BlockAccessList item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        ReadOnlySpan<AccountChanges> accounts = item.AccountChangesByAddress;
        AccountChangesDecoder.EncodingLengths[] accountLengths = ArrayPool<AccountChangesDecoder.EncodingLengths>.Shared.Rent(accounts.Length);
        try
        {
            PrepareAccountLengths(accounts, accountLengths, rlpBehaviors, out int contentLength);

            RlpStream stream = new(Rlp.LengthOfSequence(contentLength));
            EncodePrepared(stream, accounts, accountLengths, contentLength, rlpBehaviors);
            return stream.Data.ToArray();
        }
        finally
        {
            ArrayPool<AccountChangesDecoder.EncodingLengths>.Shared.Return(accountLengths);
        }
    }

    public void Encode(RlpStream stream, BlockAccessList item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        ReadOnlySpan<AccountChanges> accounts = item.AccountChangesByAddress;
        AccountChangesDecoder.EncodingLengths[] accountLengths = ArrayPool<AccountChangesDecoder.EncodingLengths>.Shared.Rent(accounts.Length);
        try
        {
            PrepareAccountLengths(accounts, accountLengths, rlpBehaviors, out int contentLength);
            EncodePrepared(stream, accounts, accountLengths, contentLength, rlpBehaviors);
        }
        finally
        {
            ArrayPool<AccountChangesDecoder.EncodingLengths>.Shared.Return(accountLengths);
        }
    }

    private static int GetContentLength(BlockAccessList item, RlpBehaviors rlpBehaviors)
    {
        int contentLength = 0;
        foreach (AccountChanges accountChanges in item.UnorderedAccountChanges)
        {
            contentLength += AccountChangesDecoder.Instance.GetLength(accountChanges, rlpBehaviors);
        }

        return contentLength;
    }

    private static void PrepareAccountLengths(
        ReadOnlySpan<AccountChanges> accounts,
        AccountChangesDecoder.EncodingLengths[] accountLengths,
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

    private static void EncodePrepared(
        RlpStream stream,
        ReadOnlySpan<AccountChanges> accounts,
        AccountChangesDecoder.EncodingLengths[] accountLengths,
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

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowEmptyAccountChanges() =>
        throw new RlpException("Empty AccountChanges entry; EIP-7928 requires a 6-field sequence.");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowAccountChangesOutOfOrder() =>
        throw new RlpException("Account changes were in incorrect order.");
}
