// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Int256;

namespace Nethermind.Serialization.Rlp.Eip7928;

/// <summary>Encodes a <see cref="MergedBlockAccessList"/> as EIP-7928 RLP.</summary>
/// <remarks>
/// The merged list is already in canonical order, so it is written as is. Two passes over the accounts: lengths
/// first, so each list's prefix is known before its content.
/// </remarks>
public static class MergedBlockAccessListEncoder
{
    public static byte[] EncodeToBytes(MergedBlockAccessList list, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int accountCount = list.AccountCount;
        using ArrayPoolListRef<AccountLengths> accountLengths = new(accountCount, accountCount);
        Span<AccountLengths> lengths = accountLengths.AsSpan();
        int contentLength = 0;
        for (int i = 0; i < accountCount; i++)
        {
            lengths[i] = new AccountLengths(list.GetAccount(i), rlpBehaviors);
            contentLength += Rlp.LengthOfSequence(lengths[i].Content);
        }

        byte[] bytes = new byte[Rlp.LengthOfSequence(contentLength)];
        RlpWriter writer = new(bytes);
        writer.StartSequence(contentLength);
        for (int i = 0; i < accountCount; i++)
        {
            EncodeAccount(ref writer, list.GetAccount(i), in lengths[i], rlpBehaviors);
        }
        return bytes;
    }

    private static void EncodeAccount(ref RlpWriter writer, scoped MergedBlockAccessList.AccountChanges account, in AccountLengths lengths, RlpBehaviors rlpBehaviors)
    {
        writer.StartSequence(lengths.Content);
        writer.Encode(account.Address);
        writer.StartSequence(lengths.StorageChanges);
        for (int slot = 0; slot < account.SlotCount; slot++)
        {
            ReadOnlySpan<StorageChange> changes = account.GetSlotChanges(slot);
            UInt256 key = account.GetSlotKey(slot);
            int changesLength = ContentLength(changes, StorageChangeDecoder.Instance, rlpBehaviors);
            writer.StartSequence(Rlp.LengthOf(key) + Rlp.LengthOfSequence(changesLength));
            writer.Encode(key);
            Encode(ref writer, changes, changesLength, StorageChangeDecoder.Instance, rlpBehaviors);
        }
        writer.StartSequence(lengths.StorageReads);
        foreach (UInt256 read in account.StorageReads) writer.Encode(read);
        Encode(ref writer, account.BalanceChanges, lengths.BalanceChanges, BalanceChangeDecoder.Instance, rlpBehaviors);
        Encode(ref writer, account.NonceChanges, lengths.NonceChanges, NonceChangeDecoder.Instance, rlpBehaviors);
        Encode(ref writer, account.CodeChanges, lengths.CodeChanges, CodeChangeDecoder.Instance, rlpBehaviors);
    }

    private static int ContentLength<T>(scoped ReadOnlySpan<T> items, IndexedChangeDecoder<T> decoder, RlpBehaviors rlpBehaviors) where T : struct, IIndexedChange
    {
        int length = 0;
        foreach (T item in items) length += decoder.GetLength(item, rlpBehaviors);
        return length;
    }

    private static void Encode<T>(ref RlpWriter writer, scoped ReadOnlySpan<T> items, int contentLength, IndexedChangeDecoder<T> decoder, RlpBehaviors rlpBehaviors) where T : struct, IIndexedChange
    {
        writer.StartSequence(contentLength);
        foreach (T item in items) decoder.Encode(ref writer, item, rlpBehaviors);
    }

    private readonly struct AccountLengths
    {
        public readonly int Content;
        public readonly int StorageChanges;
        public readonly int StorageReads;
        public readonly int BalanceChanges;
        public readonly int NonceChanges;
        public readonly int CodeChanges;

        public AccountLengths(scoped MergedBlockAccessList.AccountChanges account, RlpBehaviors rlpBehaviors)
        {
            for (int slot = 0; slot < account.SlotCount; slot++)
            {
                UInt256 key = account.GetSlotKey(slot);
                StorageChanges += Rlp.LengthOfSequence(Rlp.LengthOf(key) + Rlp.LengthOfSequence(ContentLength(account.GetSlotChanges(slot), StorageChangeDecoder.Instance, rlpBehaviors)));
            }
            foreach (UInt256 read in account.StorageReads) StorageReads += Rlp.LengthOf(read);
            BalanceChanges = ContentLength(account.BalanceChanges, BalanceChangeDecoder.Instance, rlpBehaviors);
            NonceChanges = ContentLength(account.NonceChanges, NonceChangeDecoder.Instance, rlpBehaviors);
            CodeChanges = ContentLength(account.CodeChanges, CodeChangeDecoder.Instance, rlpBehaviors);
            Content = Rlp.LengthOfAddressRlp
                + Rlp.LengthOfSequence(StorageChanges)
                + Rlp.LengthOfSequence(StorageReads)
                + Rlp.LengthOfSequence(BalanceChanges)
                + Rlp.LengthOfSequence(NonceChanges)
                + Rlp.LengthOfSequence(CodeChanges);
        }
    }
}
