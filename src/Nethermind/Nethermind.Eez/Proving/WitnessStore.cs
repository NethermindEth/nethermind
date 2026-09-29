// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Autofac.Features.AttributeFilters;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Eez.Proving;

/// <summary>The execution witnesses of the blocks the sequencer committed, kept until L1 finalizes their settlement.</summary>
public interface IWitnessStore
{
    void Put(BlockHeader header, Witness witness);

    /// <returns>The witness of the block, or <see langword="null"/> when it was never stored or is pruned.</returns>
    Witness? Find(ulong number, Hash256 hash);

    /// <summary>Removes the witnesses of every block below <paramref name="number"/>.</summary>
    void PruneBelow(ulong number);
}

/// <summary>
/// Stores witnesses keyed by block number then hash, so pruning is an ordered scan from the lowest block and siblings
/// of a replaced block never collide. A witness is its four lists, state, codes, keys and headers, as an RLP sequence.
/// </summary>
public sealed class WitnessStore([KeyFilter(WitnessStore.DbName)] IDb db) : IWitnessStore
{
    public const string DbName = "EezWitnesses";

    private const int KeySize = sizeof(ulong) + Hash256.Size;

    public void Put(BlockHeader header, Witness witness)
    {
        Span<byte> key = stackalloc byte[KeySize];
        db.PutSpan(Key(key, header.Number, header.Hash!), Encode(witness));
    }

    public Witness? Find(ulong number, Hash256 hash)
    {
        Span<byte> key = stackalloc byte[KeySize];
        byte[]? value = db.Get(Key(key, number, hash));
        return value is null ? null : Decode(value);
    }

    public void PruneBelow(ulong number)
    {
        using IWriteBatch batch = db.StartWriteBatch();
        foreach (KeyValuePair<byte[], byte[]> entry in db.GetAll(ordered: true))
        {
            if (BinaryPrimitives.ReadUInt64BigEndian(entry.Key) >= number)
            {
                break;
            }

            batch.Remove(entry.Key);
        }
    }

    private static ReadOnlySpan<byte> Key(Span<byte> key, ulong number, Hash256 hash)
    {
        BinaryPrimitives.WriteUInt64BigEndian(key, number);
        hash.Bytes.CopyTo(key[sizeof(ulong)..]);
        return key;
    }

    private static byte[] Encode(Witness witness) =>
        Rlp.Encode(EncodeList(witness.State), EncodeList(witness.Codes), EncodeList(witness.Keys), EncodeList(witness.Headers)).Bytes;

    private static Rlp EncodeList(IReadOnlyList<byte[]> items)
    {
        Rlp[] encoded = new Rlp[items.Count];
        for (int i = 0; i < encoded.Length; i++)
        {
            encoded[i] = Rlp.Encode(items[i]);
        }

        return Rlp.Encode(encoded);
    }

    private static Witness Decode(byte[] value)
    {
        RlpReader reader = new(value);
        reader.ReadSequenceLength();
        return new Witness
        {
            State = ReadList(ref reader),
            Codes = ReadList(ref reader),
            Keys = ReadList(ref reader),
            Headers = ReadList(ref reader),
        };
    }

    private static ArrayPoolList<byte[]> ReadList(ref RlpReader reader)
    {
        int end = reader.ReadSequenceLength() + reader.Position;
        ArrayPoolList<byte[]> items = new(8);
        while (reader.Position < end)
        {
            items.Add(reader.DecodeByteArray());
        }

        return items;
    }
}
