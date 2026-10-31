// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.SyncLimits;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V72.Messages;

public class GetCellsMessageSerializer72 : IZeroInnerMessageSerializer<GetCellsMessage72>
{
    // The wire limit remains permissive, but only the locally supported prefix is materialized.
    private static readonly RlpLimit HashesRlpLimit = RlpLimit.For<GetCellsMessage72>(NethermindSyncLimits.MaxHashesFetch, nameof(GetCellsMessage72.Hashes));

    public void Serialize(IByteBuffer byteBuffer, GetCellsMessage72 message)
    {
        int totalLength = GetLength(message, out int contentLength);
        RlpWriter writer = byteBuffer.StartRlpWrite(totalLength);
        RlpSequence sequence = writer.StartSequence(contentLength);
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
        byteBuffer.CompleteRlpWrite(in writer, totalLength);
    }

    public GetCellsMessage72 Deserialize(IByteBuffer byteBuffer) => byteBuffer.DeserializeRlp(Deserialize);

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

    public int GetLength(GetCellsMessage72 message, out int contentLength)
    {
        contentLength = Rlp.LengthOf(message.RequestId)
            + Rlp.LengthOfSequence(GetHashesContentLength(message.Hashes))
            + Rlp.LengthOf(message.CellMask);
        return Rlp.LengthOfSequence(contentLength);
    }

    private static int GetHashesContentLength(ValueHash256[] hashes)
        => checked(hashes.Length * Rlp.LengthOfKeccakRlp);
}
