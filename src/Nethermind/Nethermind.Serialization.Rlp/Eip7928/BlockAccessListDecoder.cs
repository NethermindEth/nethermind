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

    public override void PopulateLength(ReadOnlyBlockAccessList item, ref LengthStack lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        ArgumentNullException.ThrowIfNull(item);
        AccountChangesDecoder.Instance.PopulateSequenceLength(item.AccountChanges.AsSpan(), ref lengths, rlpBehaviors);
    }

    public override void Encode<TWriter>(ReadOnlyBlockAccessList item, ref LengthStack lengths, ref TWriter writer, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        ArgumentNullException.ThrowIfNull(item);
        AccountChangesDecoder.Instance.EncodeSequence(item.AccountChanges.AsSpan(), ref lengths, ref writer, rlpBehaviors);
    }

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
    /// RLP encode of a generated BAL into a freshly allocated byte buffer.
    /// </summary>
    public static byte[] EncodeToBytes(GeneratedBlockAccessList item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        using ArrayPoolListRef<GeneratedAccountChanges> sortedAccounts = item.GetSortedAccountChanges();
        LengthStack lengths = new(stackalloc int[LengthStack.DefaultStackallocLength]);
        try
        {
            PopulateLength(sortedAccounts.AsSpan(), ref lengths, rlpBehaviors);
            byte[] bytes = new byte[lengths.Peek()];
            EncodeTo(sortedAccounts.AsSpan(), ref lengths, bytes, rlpBehaviors);
            return bytes;
        }
        finally
        {
            lengths.Dispose();
        }
    }

    /// <summary>
    /// Encodes <paramref name="item"/> into a pool-rented byte span. The caller owns the result and must dispose it.
    /// </summary>
    public static ArrayPoolSpan<byte> EncodeToArrayPoolSpan(
        GeneratedBlockAccessList item,
        RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        using ArrayPoolListRef<GeneratedAccountChanges> sortedAccounts = item.GetSortedAccountChanges();
        LengthStack lengths = new(stackalloc int[LengthStack.DefaultStackallocLength]);
        try
        {
            PopulateLength(sortedAccounts.AsSpan(), ref lengths, rlpBehaviors);
            ArrayPoolSpan<byte> bytes = new(lengths.Peek());
            try
            {
                EncodeTo(sortedAccounts.AsSpan(), ref lengths, bytes, rlpBehaviors);
                return bytes;
            }
            catch
            {
                bytes.Dispose();
                throw;
            }
        }
        finally
        {
            lengths.Dispose();
        }
    }

    /// <inheritdoc cref="EncodeToBytes(GeneratedBlockAccessList, RlpBehaviors)"/>
    public static byte[] EncodeToBytes(ReadOnlyBlockAccessList item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        => Instance.EncodeAsBytes(item, rlpBehaviors);

    private static void PopulateLength(scoped ReadOnlySpan<GeneratedAccountChanges> sortedAccounts, ref LengthStack lengths, RlpBehaviors rlpBehaviors)
    {
        AccountChangesDecoder accountChangesDecoder = AccountChangesDecoder.Instance;
        int contentLength = 0;
        for (int i = sortedAccounts.Length - 1; i >= 0; i--)
        {
            accountChangesDecoder.PopulateLength(sortedAccounts[i], ref lengths, rlpBehaviors);
            contentLength += lengths.Peek();
        }

        lengths.Push(contentLength);
        lengths.Push(Rlp.LengthOfSequence(contentLength));
    }

    private static void EncodeTo(scoped ReadOnlySpan<GeneratedAccountChanges> sortedAccounts, ref LengthStack lengths, Span<byte> destination, RlpBehaviors rlpBehaviors)
    {
        RlpWriter writer = new(destination);
        Encode(sortedAccounts, ref lengths, ref writer, rlpBehaviors);
    }

    private static void Encode<TWriter>(scoped ReadOnlySpan<GeneratedAccountChanges> sortedAccounts, ref LengthStack lengths, ref TWriter writer, RlpBehaviors rlpBehaviors)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        lengths.Pop();
        writer.StartSequence(lengths.Pop());
        AccountChangesDecoder accountChangesDecoder = AccountChangesDecoder.Instance;
        for (int i = 0; i < sortedAccounts.Length; i++)
        {
            accountChangesDecoder.Encode(sortedAccounts[i], ref lengths, ref writer, rlpBehaviors);
        }
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowAccountChangesOutOfOrder() =>
        throw new RlpException("Account changes were in incorrect order.");

}
