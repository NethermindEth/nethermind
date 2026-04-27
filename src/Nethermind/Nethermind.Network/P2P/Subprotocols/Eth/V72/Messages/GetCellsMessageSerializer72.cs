// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.SyncLimits;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V72.Messages;

public class GetCellsMessageSerializer72 : IZeroMessageSerializer<GetCellsMessage72>
{
    private static readonly RlpLimit HashesRlpLimit = RlpLimit.For<GetCellsMessage72>(NethermindSyncLimits.MaxHashesFetch, nameof(GetCellsMessage72.Hashes));

    public GetCellsMessage72 Deserialize(IByteBuffer byteBuffer) =>
        byteBuffer.DeserializeRlp(Deserialize);

    private static GetCellsMessage72 Deserialize(ref Rlp.ValueDecoderContext ctx)
    {
        ctx.ReadSequenceLength();
        using ArrayPoolList<Hash256> hashes = ctx.DecodeArrayPoolList(static (ref Rlp.ValueDecoderContext c) => c.DecodeKeccak(), limit: HashesRlpLimit);
        byte[] cellMask = ctx.DecodeByteArraySpan().ToArray();
        if (cellMask.Length != BlobCellMask.FixedByteLength)
        {
            throw new RlpException($"Invalid cell mask length in {nameof(GetCellsMessage72)}: expected {BlobCellMask.FixedByteLength}, got {cellMask.Length}.");
        }

        return new GetCellsMessage72(hashes.AsSpan().ToArray(), cellMask);
    }

    public void Serialize(IByteBuffer byteBuffer, GetCellsMessage72 message)
    {
        int hashesLength = 0;
        foreach (Hash256 hash in message.Hashes)
        {
            hashesLength += Rlp.LengthOf(hash);
        }

        int totalSize = Rlp.LengthOfSequence(hashesLength) + Rlp.LengthOf(message.CellMask);
        byteBuffer.EnsureWritable(totalSize);

        RlpStream rlpStream = new NettyRlpStream(byteBuffer);
        rlpStream.StartSequence(totalSize);
        rlpStream.StartSequence(hashesLength);
        foreach (Hash256 hash in message.Hashes)
        {
            rlpStream.Encode(hash);
        }

        rlpStream.Encode(message.CellMask);
    }
}
