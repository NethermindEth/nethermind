// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;

namespace Nethermind.State.Flat.History.Proofs;

internal static class AccountRowRlp
{
    public static byte[] Encode(ReadOnlySpan<byte> slimRow)
    {
        RlpReader reader = new(slimRow);
        if (!AccountDecoder.Slim.TryDecodeStruct(ref reader, out AccountStruct account))
        {
            throw new InvalidDataException("An account history row failed to decode; the history column is corrupt.");
        }

        Span<byte> nonce = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(nonce, account.Nonce);
        nonce = nonce.TrimStart((byte)0);
        Span<byte> balance = stackalloc byte[Hash256.Size];
        account.Balance.ToBigEndian(balance);
        balance = balance.TrimStart((byte)0);

        int contentLength = Rlp.LengthOf(nonce) + Rlp.LengthOf(balance) + 2 * (1 + Hash256.Size);
        byte[] rlp = new byte[Rlp.LengthOfSequence(contentLength)];
        int position = Rlp.StartSequence(rlp, 0, contentLength);
        position = Rlp.Encode(rlp, position, nonce);
        position = Rlp.Encode(rlp, position, balance);
        position = Rlp.Encode(rlp, position, account.StorageRoot.Bytes);
        Rlp.Encode(rlp, position, account.CodeHash.Bytes);
        return rlp;
    }

    public static void Set(StateTree state, in ValueHash256 path, ReadOnlySpan<byte> slimRow)
    {
        if (slimRow.IsEmpty) state.Set(path.Bytes, CappedArray<byte>.Empty);
        else state.Set(path.Bytes, new CappedArray<byte>(Encode(slimRow)));
    }

    public static void SetSlot(StorageTree tree, in ValueHash256 slot, ReadOnlySpan<byte> value, bool rlpWrapped)
    {
        if (value.IsZero())
        {
            tree.Set(slot.Bytes, CappedArray<byte>.Empty);
            return;
        }

        byte[] stored;
        if (rlpWrapped)
        {
            stored = value.ToArray();
        }
        else
        {
            stored = new byte[Rlp.LengthOf(value)];
            Rlp.Encode(stored, 0, value);
        }

        tree.Set(slot.Bytes, new CappedArray<byte>(stored));
    }
}
