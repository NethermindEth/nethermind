// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
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
        ReadOnlyAccountChanges[] accountChanges = ctx.DecodeArray(AccountChangesDecoder.Instance, true, default, _accountsLimit);
        ReadOnlySpan<byte> wireRlp = ctx.Data.Slice(startPosition, ctx.Position - startPosition);

        Address? lastAddress = null;
        int itemCount = 0;
        foreach (ReadOnlyAccountChanges a in accountChanges)
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

            itemCount += 1 + a.StorageChanges.Length + a.StorageReads.Length;
        }

        Hash256 wireHash = new(ValueKeccak.Compute(wireRlp));
        return new ReadOnlyBlockAccessList(accountChanges, itemCount, wireHash);
    }

    /// <summary>One-pass RLP encode of a generated BAL into a freshly allocated byte buffer.
    /// Used on the hot path that finalises the BAL hash for each block. Computes every account's
    /// sub-sequence content lengths once into a rented <see cref="ArrayPool{T}"/> buffer so the
    /// encode pass doesn't re-walk per-account collections.</summary>
    public static byte[] EncodeToBytes(GeneratedBlockAccessList item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int accountCount = item.AccountChanges.Count;
        AccountChangesDecoder.EncodingLengths[] accountLengths = ArrayPool<AccountChangesDecoder.EncodingLengths>.Shared.Rent(accountCount);

        try
        {
            PrepareGeneratedLengths(item, accountLengths, rlpBehaviors, out int contentLength);

            RlpStream stream = new(Rlp.LengthOfSequence(contentLength));
            EncodeGeneratedPrepared(stream, item, accountLengths, contentLength, rlpBehaviors);
            return stream.Data.ToArray();
        }
        finally
        {
            ArrayPool<AccountChangesDecoder.EncodingLengths>.Shared.Return(accountLengths);
        }
    }

    /// <inheritdoc cref="EncodeToBytes(GeneratedBlockAccessList, RlpBehaviors)"/>
    public static byte[] EncodeToBytes(ReadOnlyBlockAccessList item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int accountCount = item.AccountChanges.Count;
        AccountChangesDecoder.EncodingLengths[] accountLengths = ArrayPool<AccountChangesDecoder.EncodingLengths>.Shared.Rent(accountCount);

        try
        {
            PrepareReadOnlyLengths(item, accountLengths, rlpBehaviors, out int contentLength);

            RlpStream stream = new(Rlp.LengthOfSequence(contentLength));
            EncodeReadOnlyPrepared(stream, item, accountLengths, contentLength, rlpBehaviors);
            return stream.Data.ToArray();
        }
        finally
        {
            ArrayPool<AccountChangesDecoder.EncodingLengths>.Shared.Return(accountLengths);
        }
    }

    public override void Encode(RlpStream stream, ReadOnlyBlockAccessList item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int accountCount = item.AccountChanges.Count;
        AccountChangesDecoder.EncodingLengths[] accountLengths = ArrayPool<AccountChangesDecoder.EncodingLengths>.Shared.Rent(accountCount);
        try
        {
            PrepareReadOnlyLengths(item, accountLengths, rlpBehaviors, out int contentLength);
            EncodeReadOnlyPrepared(stream, item, accountLengths, contentLength, rlpBehaviors);
        }
        finally
        {
            ArrayPool<AccountChangesDecoder.EncodingLengths>.Shared.Return(accountLengths);
        }
    }

    public void Encode(RlpStream stream, GeneratedBlockAccessList item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int accountCount = item.AccountChanges.Count;
        AccountChangesDecoder.EncodingLengths[] accountLengths = ArrayPool<AccountChangesDecoder.EncodingLengths>.Shared.Rent(accountCount);
        try
        {
            PrepareGeneratedLengths(item, accountLengths, rlpBehaviors, out int contentLength);
            EncodeGeneratedPrepared(stream, item, accountLengths, contentLength, rlpBehaviors);
        }
        finally
        {
            ArrayPool<AccountChangesDecoder.EncodingLengths>.Shared.Return(accountLengths);
        }
    }

    private static int GetContentLength(ReadOnlyBlockAccessList item, RlpBehaviors rlpBehaviors)
    {
        int contentLength = 0;
        foreach (ReadOnlyAccountChanges a in item.AccountChanges)
        {
            contentLength += AccountChangesDecoder.Instance.GetLength(a, rlpBehaviors);
        }
        return contentLength;
    }

    private static int GetContentLength(GeneratedBlockAccessList item, RlpBehaviors rlpBehaviors)
    {
        int contentLength = 0;
        foreach (GeneratedAccountChanges a in item.AccountChanges)
        {
            contentLength += AccountChangesDecoder.Instance.GetLength(a, rlpBehaviors);
        }
        return contentLength;
    }

    private static void PrepareReadOnlyLengths(
        ReadOnlyBlockAccessList item,
        AccountChangesDecoder.EncodingLengths[] accountLengths,
        RlpBehaviors rlpBehaviors,
        out int contentLength)
    {
        Debug.Assert(accountLengths.Length >= item.AccountChanges.Count);

        contentLength = 0;
        int i = 0;
        foreach (ReadOnlyAccountChanges a in item.AccountChanges)
        {
            AccountChangesDecoder.EncodingLengths accountLength = AccountChangesDecoder.PrepareEncodingLengths(a, rlpBehaviors);
            accountLengths[i++] = accountLength;
            contentLength += Rlp.LengthOfSequence(accountLength.ContentLength);
        }
    }

    private static void PrepareGeneratedLengths(
        GeneratedBlockAccessList item,
        AccountChangesDecoder.EncodingLengths[] accountLengths,
        RlpBehaviors rlpBehaviors,
        out int contentLength)
    {
        Debug.Assert(accountLengths.Length >= item.AccountChanges.Count);

        contentLength = 0;
        int i = 0;
        foreach (GeneratedAccountChanges a in item.AccountChanges)
        {
            AccountChangesDecoder.EncodingLengths accountLength = AccountChangesDecoder.PrepareEncodingLengths(a, rlpBehaviors);
            accountLengths[i++] = accountLength;
            contentLength += Rlp.LengthOfSequence(accountLength.ContentLength);
        }
    }

    private static void EncodeReadOnlyPrepared(
        RlpStream stream,
        ReadOnlyBlockAccessList item,
        AccountChangesDecoder.EncodingLengths[] accountLengths,
        int contentLength,
        RlpBehaviors rlpBehaviors)
    {
        Debug.Assert(accountLengths.Length >= item.AccountChanges.Count);

        stream.StartSequence(contentLength);
        AccountChangesDecoder accountChangesDecoder = AccountChangesDecoder.Instance;
        int i = 0;
        foreach (ReadOnlyAccountChanges a in item.AccountChanges)
        {
            accountChangesDecoder.EncodePrepared(stream, a, in accountLengths[i++], rlpBehaviors);
        }
    }

    private static void EncodeGeneratedPrepared(
        RlpStream stream,
        GeneratedBlockAccessList item,
        AccountChangesDecoder.EncodingLengths[] accountLengths,
        int contentLength,
        RlpBehaviors rlpBehaviors)
    {
        Debug.Assert(accountLengths.Length >= item.AccountChanges.Count);

        stream.StartSequence(contentLength);
        AccountChangesDecoder accountChangesDecoder = AccountChangesDecoder.Instance;
        int i = 0;
        foreach (GeneratedAccountChanges a in item.AccountChanges)
        {
            accountChangesDecoder.EncodePrepared(stream, a, in accountLengths[i++], rlpBehaviors);
        }
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowEmptyAccountChanges() =>
        throw new RlpException("Empty AccountChanges entry; EIP-7928 requires a 6-field sequence.");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowAccountChangesOutOfOrder() =>
        throw new RlpException("Account changes were in incorrect order.");
}
