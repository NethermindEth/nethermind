// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Snap;
using Nethermind.Trie;

namespace Nethermind.State.SnapServer;

/// <summary>Collects storage leaves as the encoded <c>[path, slot value]</c> entries of a storage range response.</summary>
/// <param name="slots">The list that receives the entries, or <see langword="null"/> for a new list.</param>
public class PathWithStorageCollector(EncodedSnapEntries<PathWithStorageSlot>? slots = null) : RangeQueryVisitor.ILeafValueCollector
{
    public EncodedSnapEntries<PathWithStorageSlot> Slots { get; } = slots ?? new(DecodeEntry);

    public int Collect(in ValueHash256 path, ReadOnlySpan<byte> value)
    {
        int contentLength = Rlp.LengthOfKeccakRlp + Rlp.LengthOf(value);
        RlpWriter writer = RlpWriter.ContentWriter(Slots.Add(Rlp.LengthOfSequence(contentLength)));
        RlpSequence entry = writer.StartSequence(contentLength);
        writer.Encode(path);
        writer.Write(value);
        writer.EndSequence(entry);
        return 32 + value.Length;
    }

    internal static PathWithStorageSlot DecodeEntry(scoped ref RlpReader reader)
    {
        RlpReader entry = reader.EnterSequence(out int length);
        ValueHash256 path = entry.DecodeValueKeccakNonNull();
        byte[] value = entry.DecodeByteArray();
        reader.ExitSequence(entry, length);
        return new PathWithStorageSlot(in path, value);
    }
}
