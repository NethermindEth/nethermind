// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.SyncLimits;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V72.Messages;

public class GetCellsMessageSerializer72 : IMessageSerializer<GetCellsMessage72>
{
    // The wire limit remains permissive, but only the locally supported prefix is materialized.
    private static readonly RlpLimit HashesRlpLimit = RlpLimit.For<GetCellsMessage72>(NethermindSyncLimits.MaxHashesFetch, nameof(GetCellsMessage72.Hashes));

    public int Serialize(Span<byte> destination, GetCellsMessage72 message)
    {
        RlpWriter writer = new(destination);
        writer.StartSequence(GetContentLength(message));
        writer.Encode(message.RequestId);

        int hashesLength = GetHashesContentLength(message.Hashes);
        writer.StartSequence(hashesLength);

        foreach (ValueHash256 hash in message.Hashes)
        {
            writer.Encode(hash);
        }

        writer.Encode(message.CellMask);
        return writer.Position;
    }

    public GetCellsMessage72 Deserialize(ReadOnlySpan<byte> data)
    {
        RlpReader ctx = new(data);
        GetCellsMessage72 message = Deserialize(ref ctx);
        ctx.CheckEnd();
        return message;
    }

    private static GetCellsMessage72 Deserialize(ref RlpReader ctx)
    {
        int sequenceLength = ctx.ReadSequenceLength();
        int checkPosition = ctx.Position + sequenceLength;
        long requestId = ctx.DecodeLong();

        int hashesCheckPosition = ctx.ReadSequenceLength() + ctx.Position;
        int hashCount = ctx.PeekNumberOfItemsRemaining(hashesCheckPosition, HashesRlpLimit.Limit + 1);
        ctx.GuardLimit(hashCount, HashesRlpLimit);
        int retainedHashCount = Math.Min(hashCount, Eth72ProtocolHandler.MaxCellsRequestHashes);
        ValueHash256[] hashes = new ValueHash256[retainedHashCount];
        for (int i = 0; i < retainedHashCount; i++)
        {
            hashes[i] = ctx.DecodeValueKeccakNonNull();
        }

        for (int i = retainedHashCount; i < hashCount; i++)
        {
            ctx.DecodeByteArraySpan(size: Hash256.Size);
        }

        ctx.Check(hashesCheckPosition);
        byte[] cellMask = ctx.DecodeByteArray(size: BlobCellMask.FixedByteLength);

        ctx.Check(checkPosition);
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
