// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V76.Messages;

/// <remarks>Each wrapper is kept as its encoded RLP item and decoded only once matched against its request, which may
/// resolve compact references from the transaction encodings this node holds.</remarks>
public sealed class ProofWrappersMessageSerializer : Eth66SerializerBase<ProofWrappersMessage>
{
    protected override void SerializeInternal(ref RlpWriter writer, ProofWrappersMessage message)
    {
        RlpSequence sequence = writer.StartSequence(ContentLength(message.Wrappers));
        foreach (ServedWrapper wrapper in message.Wrappers)
        {
            RlpSequence entry = writer.StartSequence(Rlp.LengthOfKeccakRlp + wrapper.Item.Length);
            writer.Encode(wrapper.Hash);
            writer.WriteEncoded(wrapper.Item);
            writer.EndSequence(entry);
        }

        writer.EndSequence(sequence);
    }

    protected override ProofWrappersMessage DeserializeInternal(ref RlpReader ctx, long requestId)
    {
        RlpReader reader = ctx.EnterSequence(out int length);
        int count = reader.CountItems(Eth76Protocol.MaxRequestedWrappers);
        List<ServedWrapper> wrappers = [with(count)];
        while (reader.HasNext)
        {
            RlpReader entry = reader.EnterSequence(out int entryLength);
            ValueHash256 hash = entry.DecodeValueKeccakNonNull();
            if (!entry.IsNextSequence) throw new RlpReaderException("A served proof wrapper must be an RLP list.");
            byte[] item = entry.ReadEncodedItem().ToArray();
            if (item.Length > ProofWrapperCodec.MaxWrapperBytes) throw new RlpReaderException("A served proof wrapper exceeds MAX_WRAPPER_BYTES.");
            reader.ExitSequence(entry, entryLength);
            wrappers.Add(new ServedWrapper(hash, item));
        }

        ctx.ExitSequence(reader, length);
        return new ProofWrappersMessage(requestId, wrappers);
    }

    protected override int GetLengthInternal(ProofWrappersMessage message) => Rlp.LengthOfSequence(ContentLength(message.Wrappers));

    private static int ContentLength(IReadOnlyList<ServedWrapper> wrappers)
    {
        int length = 0;
        foreach (ServedWrapper wrapper in wrappers) length += Rlp.LengthOfSequence(Rlp.LengthOfKeccakRlp + wrapper.Item.Length);
        return length;
    }
}

public sealed class ProofWrapperManifestsMessageSerializer : Eth66SerializerBase<ProofWrapperManifestsMessage>
{
    private const int MaxManifestHashes = ProofWrapperCodec.MaxWrapperBytes / Rlp.LengthOfKeccakRlp;

    protected override void SerializeInternal(ref RlpWriter writer, ProofWrapperManifestsMessage message)
    {
        RlpSequence sequence = writer.StartSequence(ContentLength(message.Manifests));
        foreach (ServedManifest manifest in message.Manifests)
        {
            int hashesLength = manifest.TransactionHashes.Count * Rlp.LengthOfKeccakRlp;
            RlpSequence entry = writer.StartSequence(Rlp.LengthOfKeccakRlp + Rlp.LengthOfSequence(hashesLength));
            writer.Encode(manifest.Hash);
            RlpSequence hashes = writer.StartSequence(hashesLength);
            foreach (ValueHash256 txHash in manifest.TransactionHashes) writer.Encode(txHash);
            writer.EndSequence(hashes);
            writer.EndSequence(entry);
        }

        writer.EndSequence(sequence);
    }

    protected override ProofWrapperManifestsMessage DeserializeInternal(ref RlpReader ctx, long requestId)
    {
        RlpReader reader = ctx.EnterSequence(out int length);
        int count = reader.CountItems(Eth76Protocol.MaxRequestedWrappers);
        List<ServedManifest> manifests = [with(count)];
        while (reader.HasNext)
        {
            RlpReader entry = reader.EnterSequence(out int entryLength);
            ValueHash256 hash = entry.DecodeValueKeccakNonNull();
            RlpReader hashesReader = entry.EnterSequence(out int hashesLength);
            List<ValueHash256> txHashes = [with(hashesReader.CountItems(MaxManifestHashes))];
            while (hashesReader.HasNext) txHashes.Add(hashesReader.DecodeValueKeccakNonNull());
            entry.ExitSequence(hashesReader, hashesLength);
            reader.ExitSequence(entry, entryLength);
            manifests.Add(new ServedManifest(hash, txHashes));
        }

        ctx.ExitSequence(reader, length);
        return new ProofWrapperManifestsMessage(requestId, manifests);
    }

    protected override int GetLengthInternal(ProofWrapperManifestsMessage message) => Rlp.LengthOfSequence(ContentLength(message.Manifests));

    private static int ContentLength(IReadOnlyList<ServedManifest> manifests)
    {
        int length = 0;
        foreach (ServedManifest manifest in manifests)
            length += Rlp.LengthOfSequence(Rlp.LengthOfKeccakRlp + Rlp.LengthOfSequence(manifest.TransactionHashes.Count * Rlp.LengthOfKeccakRlp));
        return length;
    }
}
