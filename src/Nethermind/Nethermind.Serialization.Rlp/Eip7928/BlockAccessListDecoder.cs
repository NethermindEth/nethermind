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

    public override void PopulateLength(ReadOnlyBlockAccessList item, in LengthListWriter lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        ArgumentNullException.ThrowIfNull(item);
        AccountChangesDecoder.Instance.PopulateSequenceLength(item.AccountChanges.AsSpan(), lengths, rlpBehaviors);
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
        LengthList lengths = new(stackalloc int[Math.Min(sortedAccounts.Count * 8, LengthList.MaxStackallocLength)]);
        try
        {
            byte[] bytes = new byte[PopulateLength(sortedAccounts.AsSpan(), ref lengths, rlpBehaviors)];
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
        LengthList lengths = new(stackalloc int[Math.Min(sortedAccounts.Count * 8, LengthList.MaxStackallocLength)]);
        try
        {
            ArrayPoolSpan<byte> bytes = new(PopulateLength(sortedAccounts.AsSpan(), ref lengths, rlpBehaviors));
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

    public override void Encode<TWriter>(ref TWriter writer, ReadOnlyBlockAccessList item, ref LengthList lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        ArgumentNullException.ThrowIfNull(item);
        AccountChangesDecoder.Instance.EncodeSequence(ref writer, item.AccountChanges.AsSpan(), ref lengths, rlpBehaviors);
    }

    private static int PopulateLength(scoped ReadOnlySpan<GeneratedAccountChanges> sortedAccounts, ref LengthList lengths, RlpBehaviors rlpBehaviors)
    {
        AccountChangesDecoder accountChangesDecoder = AccountChangesDecoder.Instance;
        LengthListWriter root = lengths.BeginRootContainer();
        using (LengthListWriter sequence = root.BeginContainer())
        {
            for (int i = 0; i < sortedAccounts.Length; i++)
            {
                accountChangesDecoder.PopulateLength(sortedAccounts[i], sequence, rlpBehaviors);
            }
        }

        return root.Length;
    }

    private static void EncodeTo(scoped ReadOnlySpan<GeneratedAccountChanges> sortedAccounts, ref LengthList lengths, Span<byte> destination, RlpBehaviors rlpBehaviors)
    {
        RlpWriter writer = new(destination);
        Encode(ref writer, sortedAccounts, ref lengths, rlpBehaviors);
    }

    private static void Encode<TWriter>(ref TWriter writer, scoped ReadOnlySpan<GeneratedAccountChanges> sortedAccounts, ref LengthList lengths, RlpBehaviors rlpBehaviors)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        writer.StartSequence(lengths.Read());
        AccountChangesDecoder accountChangesDecoder = AccountChangesDecoder.Instance;
        for (int i = 0; i < sortedAccounts.Length; i++)
        {
            accountChangesDecoder.Encode(ref writer, sortedAccounts[i], ref lengths, rlpBehaviors);
        }
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowAccountChangesOutOfOrder() =>
        throw new RlpException("Account changes were in incorrect order.");

}
