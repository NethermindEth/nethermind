// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Network;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.SyncLimits;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V72.Messages;

public class CellsMessageSerializer72 : IZeroInnerMessageSerializer<CellsMessage72>
{
    private const int MaxCellsPerTransaction = BlobCellMask.CellCount * Eip7594Constants.MaxBlobsPerTx;
    private static readonly RlpLimit HashesRlpLimit = RlpLimit.For<CellsMessage72>(NethermindSyncLimits.MaxHashesFetch, nameof(CellsMessage72.Hashes));

    public void Serialize(IByteBuffer byteBuffer, CellsMessage72 message)
    {
        int totalLength = GetLength(message, out int contentLength);
        byteBuffer.EnsureWritable(totalLength);

        RlpStream rlpStream = new NettyRlpStream(byteBuffer);
        rlpStream.StartSequence(contentLength);
        rlpStream.Encode(message.RequestId);

        int payloadContentLength = GetPayloadContentLength(message);
        rlpStream.StartSequence(payloadContentLength);

        int hashesLength = GetHashesContentLength(message.Hashes);
        rlpStream.StartSequence(hashesLength);

        foreach (Hash256 hash in message.Hashes)
        {
            rlpStream.Encode(hash);
        }

        int cellsLength = GetCellsContentLength(message.Cells);
        rlpStream.StartSequence(cellsLength);

        foreach (byte[][] cells in message.Cells)
        {
            rlpStream.Encode(cells);
        }

        rlpStream.Encode(message.CellMask);
    }

    public CellsMessage72 Deserialize(IByteBuffer byteBuffer) => byteBuffer.DeserializeRlp(Deserialize);

    private static CellsMessage72 Deserialize(ref Rlp.ValueDecoderContext ctx)
    {
        int sequenceLength = ctx.ReadSequenceLength();
        int checkPosition = ctx.Position + sequenceLength;
        long requestId = ctx.DecodeLong();

        int payloadSequenceLength = ctx.ReadSequenceLength();
        int payloadCheckPosition = ctx.Position + payloadSequenceLength;
        using ArrayPoolList<Hash256> hashes = ctx.DecodeArrayPoolList(static (ref Rlp.ValueDecoderContext c) => c.DecodeKeccak(), limit: HashesRlpLimit);

        int cellsSequenceLength = ctx.ReadSequenceLength();
        int cellsEnd = ctx.Position + cellsSequenceLength;
        List<byte[][]> cellsByTx = new(hashes.Count);
        while (ctx.Position < cellsEnd)
        {
            if (cellsByTx.Count >= hashes.Count)
            {
                throw new RlpLimitException($"Too many cell groups in {nameof(CellsMessage72)}: more than {hashes.Count}.");
            }

            byte[][] cells = ctx.DecodeByteArrays();
            if (cells.Length > MaxCellsPerTransaction)
            {
                throw new RlpLimitException($"Too many cells in {nameof(CellsMessage72)} group: {cells.Length}, max {MaxCellsPerTransaction}.");
            }

            cellsByTx.Add(cells);
        }

        byte[] cellMask = ctx.DecodeByteArraySpan().ToArray();
        if (cellMask.Length != BlobCellMask.FixedByteLength)
        {
            throw new RlpException($"Invalid cell mask length in {nameof(CellsMessage72)}: expected {BlobCellMask.FixedByteLength}, got {cellMask.Length}.");
        }

        if (cellsByTx.Count != hashes.Count)
        {
            throw new RlpException($"Wrong format of {nameof(CellsMessage72)} message. Hashes count: {hashes.Count} Cells count: {cellsByTx.Count}.");
        }

        ctx.Check(payloadCheckPosition);
        ctx.Check(checkPosition);
        return new CellsMessage72(requestId, hashes.AsSpan().ToArray(), cellsByTx.ToArray(), cellMask);
    }

    public int GetLength(CellsMessage72 message, out int contentLength)
    {
        contentLength = Rlp.LengthOf(message.RequestId) + Rlp.LengthOfSequence(GetPayloadContentLength(message));
        return Rlp.LengthOfSequence(contentLength);
    }

    private static int GetPayloadContentLength(CellsMessage72 message) =>
        Rlp.LengthOfSequence(GetHashesContentLength(message.Hashes))
        + Rlp.LengthOfSequence(GetCellsContentLength(message.Cells))
        + Rlp.LengthOf(message.CellMask);

    private static int GetHashesContentLength(Hash256[] hashes)
    {
        int contentLength = 0;

        for (int i = 0; i < hashes.Length; i++)
        {
            contentLength += Rlp.LengthOf(hashes[i]);
        }

        return contentLength;
    }

    private static int GetCellsContentLength(byte[][][] cellsByTx)
    {
        int contentLength = 0;

        for (int i = 0; i < cellsByTx.Length; i++)
        {
            contentLength += Rlp.LengthOf(cellsByTx[i]);
        }

        return contentLength;
    }
}
