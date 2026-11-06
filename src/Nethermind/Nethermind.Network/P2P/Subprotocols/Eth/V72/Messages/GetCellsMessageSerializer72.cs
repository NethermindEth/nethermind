// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.SyncLimits;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V72.Messages;

public class GetCellsMessageSerializer72 : IMessageSerializer<GetCellsMessage72>
{
    // The wire limit remains permissive, but only the locally supported prefix is materialized.
    private static readonly RlpLimit HashesRlpLimit = RlpLimit.For<GetCellsMessage72>(NethermindSyncLimits.MaxHashesFetch, nameof(GetCellsMessage72.Hashes));

    public int Serialize(Span<byte> destination, GetCellsMessage72 message)
    {
        RlpWriter writer = RlpWriter.ContentWriter(destination);
        RlpSequence sequence = writer.StartSequence(GetContentLength(message));
        writer.Encode(message.RequestId);

        int hashesLength = GetHashesContentLength(message.Hashes);
        RlpSequence hashes = writer.StartSequence(hashesLength);

        foreach (ValueHash256 hash in message.Hashes)
        {
            writer.Encode(hash);
        }

        writer.EndSequence(hashes);
        writer.Encode(message.CellMask);
        writer.EndSequence(sequence);
        return writer.Length;
    }

    public GetCellsMessage72 Deserialize(ReadOnlySpan<byte> data)
    {
        RlpReader ctx = new(data);
        GetCellsMessage72 message = Deserialize(ref ctx);
        ctx.CheckEnd();
        return message;
    }

    private static GetCellsMessage72 Deserialize(ref RlpReader reader)
    {
        RlpReader ctx = reader.EnterSequence(out int itemLength);
        long requestId = ctx.DecodeLong();

        RlpReader hashesReader = ctx.EnterSequence(out int hashesItemLength);
        int hashCount = hashesReader.CountItems(HashesRlpLimit);
        int retainedHashCount = Math.Min(hashCount, Eth72ProtocolHandler.MaxCellsRequestHashes);
        ValueHash256[] hashes = new ValueHash256[retainedHashCount];
        for (int i = 0; i < retainedHashCount; i++)
        {
            hashes[i] = hashesReader.DecodeValueKeccakNonNull();
        }

        for (int i = retainedHashCount; i < hashCount; i++)
        {
            hashesReader.DecodeByteArraySpan(size: Hash256.Size);
        }

        ctx.ExitSequence(hashesReader, hashesItemLength);
        byte[] cellMask = ctx.DecodeByteArray(size: BlobCellMask.FixedByteLength);

        reader.ExitSequence(ctx, itemLength);
        return new GetCellsMessage72(requestId, hashes, cellMask, hashCount);
    }

    public int GetLength(GetCellsMessage72 message) => Rlp.LengthOfSequence(GetContentLength(message));

    private static int GetContentLength(GetCellsMessage72 message) =>
        Rlp.LengthOf(message.RequestId)
        + Rlp.LengthOfSequence(GetHashesContentLength(message.Hashes))
        + Rlp.LengthOf(message.CellMask);

    private static int GetHashesContentLength(ValueHash256[] hashes)
        => checked(hashes.Length * Rlp.LengthOfKeccakRlp);
}
