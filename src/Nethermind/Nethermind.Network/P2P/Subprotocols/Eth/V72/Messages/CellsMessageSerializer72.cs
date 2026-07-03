// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V72.Messages;

public class CellsMessageSerializer72 : IZeroInnerMessageSerializer<CellsMessage72>
{
    private const int MaxCellsPerTransaction = BlobCellMask.CellCount * Eip7594Constants.MaxBlobsPerTx;
    private static readonly RlpLimit HashesRlpLimit = RlpLimit.For<CellsMessage72>(Eth72ProtocolHandler.MaxCellsResponseHashes, nameof(CellsMessage72.Hashes));
    private static readonly RlpLimit CellsPerTransactionRlpLimit = RlpLimit.For<CellsMessage72>(MaxCellsPerTransaction, nameof(CellsMessage72.Cells));

    public void Serialize(IByteBuffer byteBuffer, CellsMessage72 message)
    {
        int totalLength = GetLength(message, out int contentLength);
        byteBuffer.EnsureWritable(totalLength);

        ByteBufferRlpWriter writer = new(byteBuffer);
        writer.StartSequence(contentLength);
        writer.Encode(message.RequestId);

        int payloadContentLength = GetPayloadContentLength(message);
        writer.StartSequence(payloadContentLength);

        int hashesLength = Rlp.LengthOf(message.Hashes);
        writer.StartSequence(hashesLength);

        foreach (Hash256 hash in message.Hashes)
        {
            writer.Encode(hash);
        }

        int cellsLength = GetCellsContentLength(message.Cells);
        writer.StartSequence(cellsLength);

        foreach (byte[][] cells in message.Cells)
        {
            writer.Encode(cells);
        }

        writer.Encode(message.CellMask);
    }

    public CellsMessage72 Deserialize(IByteBuffer byteBuffer) => byteBuffer.DeserializeRlp(Deserialize);

    private static CellsMessage72 Deserialize(ref RlpReader ctx)
    {
        int sequenceLength = ctx.ReadSequenceLength();
        int checkPosition = ctx.Position + sequenceLength;
        long requestId = ctx.DecodeLong();

        int payloadSequenceLength = ctx.ReadSequenceLength();
        int payloadCheckPosition = ctx.Position + payloadSequenceLength;
        using ArrayPoolList<Hash256> hashes = ctx.DecodeArrayPoolList(static (ref RlpReader c) => c.DecodeKeccak(), limit: HashesRlpLimit);

        int cellsSequenceLength = ctx.ReadSequenceLength();
        if (cellsSequenceLength > Eth72ProtocolHandler.MinCellsResponseBytes)
        {
            throw new RlpLimitException($"Too much cell data in {nameof(CellsMessage72)}: {cellsSequenceLength}.");
        }

        int cellsEnd = ctx.Position + cellsSequenceLength;
        List<byte[][]> cellsByTx = new(hashes.Count);
        while (ctx.Position < cellsEnd)
        {
            if (cellsByTx.Count >= Eth72ProtocolHandler.MaxCellsResponseHashes)
            {
                throw new RlpLimitException($"Too many cell groups in {nameof(CellsMessage72)}: more than {Eth72ProtocolHandler.MaxCellsResponseHashes}.");
            }

            cellsByTx.Add(ctx.DecodeByteArrays(CellsPerTransactionRlpLimit));
        }

        byte[] cellMask = ctx.DecodeByteArray(size: BlobCellMask.FixedByteLength);

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
        Rlp.LengthOfSequence(Rlp.LengthOf(message.Hashes))
        + Rlp.LengthOfSequence(GetCellsContentLength(message.Cells))
        + Rlp.LengthOf(message.CellMask);

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
