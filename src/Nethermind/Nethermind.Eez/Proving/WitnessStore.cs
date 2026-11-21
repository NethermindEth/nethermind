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
using Nethermind.Serialization.FluentRlp;
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
/// Stores witnesses keyed by block number then hash, so pruning drops one key range and siblings of a replaced block
/// never collide. A witness is its four lists, state, codes, keys and headers, as an RLP sequence.
/// </summary>
public sealed class WitnessStore([KeyFilter(WitnessStore.DbName)] IDb db) : IWitnessStore
{
    public const string DbName = "EezWitnesses";

    private const int KeySize = sizeof(ulong) + Hash256.Size;

    private ulong _prunedBelow;

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
        if (number <= _prunedBelow)
        {
            return;
        }

        db.DeleteBlockNumberRange(_prunedBelow, number);
        db.ReclaimBlockNumberRange(_prunedBelow, number);
        _prunedBelow = number;
    }

    private static ReadOnlySpan<byte> Key(Span<byte> key, ulong number, Hash256 hash)
    {
        BinaryPrimitives.WriteUInt64BigEndian(key, number);
        hash.Bytes.CopyTo(key[sizeof(ulong)..]);
        return key;
    }

    private static byte[] Encode(Witness witness)
    {
        int stateLength = ContentLength(witness.State);
        int codesLength = ContentLength(witness.Codes);
        int keysLength = ContentLength(witness.Keys);
        int headersLength = ContentLength(witness.Headers);
        int length = Rlp.LengthOfSequence(stateLength) + Rlp.LengthOfSequence(codesLength) +
            Rlp.LengthOfSequence(keysLength) + Rlp.LengthOfSequence(headersLength);
        byte[] bytes = new byte[Rlp.LengthOfSequence(length)];
        RlpWriter writer = RlpWriter.ContentWriter(bytes);
        RlpSequence sequence = writer.StartSequence(length);
        EncodeList(ref writer, witness.State, stateLength);
        EncodeList(ref writer, witness.Codes, codesLength);
        EncodeList(ref writer, witness.Keys, keysLength);
        EncodeList(ref writer, witness.Headers, headersLength);
        writer.EndSequence(sequence);
        return bytes;
    }

    private static int ContentLength(IReadOnlyList<byte[]> items)
    {
        int length = 0;
        foreach (byte[] item in items) length += Rlp.LengthOf(item);
        return length;
    }

    private static void EncodeList(ref RlpWriter writer, IReadOnlyList<byte[]> items, int length)
    {
        RlpSequence sequence = writer.StartSequence(length);
        foreach (byte[] item in items) writer.Encode(item);
        writer.EndSequence(sequence);
    }

    private static Witness Decode(byte[] value)
    {
        RlpReader outer = new(value);
        RlpReader reader = outer.EnterSequence(out int length);
        if (length != value.Length || reader.CountItems(4) != 4)
            throw new RlpReaderException("Stored witness must contain exactly four lists.");
        ArrayPoolList<byte[]>? state = null, codes = null, keys = null, headers = null;
        try
        {
            state = ReadList(ref reader);
            codes = ReadList(ref reader);
            keys = ReadList(ref reader);
            headers = ReadList(ref reader);
            outer.ExitSequence(in reader, length);
            outer.CheckEnd();
            return new Witness { State = state, Codes = codes, Keys = keys, Headers = headers };
        }
        catch (RlpReaderException)
        {
            state?.Dispose();
            codes?.Dispose();
            keys?.Dispose();
            headers?.Dispose();
            throw;
        }
    }

    private static ArrayPoolList<byte[]> ReadList(ref RlpReader reader)
    {
        RlpReader values = reader.EnterSequence(out int length);
        ArrayPoolList<byte[]> items = [with(8)];
        try
        {
            while (values.HasNext)
            {
                items.Add(values.DecodeByteArray());
            }
            reader.ExitSequence(in values, length);
            return items;
        }
        catch (RlpReaderException)
        {
            items.Dispose();
            throw;
        }
    }
}
