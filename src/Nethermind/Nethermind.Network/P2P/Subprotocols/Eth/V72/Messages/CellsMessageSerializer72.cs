// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using CkzgLib;
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

        int hashesLength = Rlp.LengthOf(message.Hashes);
        writer.StartSequence(hashesLength);

        foreach (Hash256 hash in message.Hashes)
        {
            writer.Encode(hash);
        }

        int cellsLength = GetCellsContentLength(message.Cells);
        writer.StartSequence(cellsLength);

        int cellIndexCount = message.CellMask.Length == BlobCellMask.FixedByteLength
            ? BlobCellMask.FromBytes(message.CellMask).Count
            : 0;
        foreach (byte[][] cells in message.Cells)
        {
            EncodeCellGroup(ref writer, cells, cellIndexCount);
        }

        writer.Encode(message.CellMask);
    }

    public CellsMessage72 Deserialize(IByteBuffer byteBuffer) => byteBuffer.DeserializeRlp(Deserialize);

    private static CellsMessage72 Deserialize(ref RlpReader ctx)
    {
        int sequenceLength = ctx.ReadSequenceLength();
        int checkPosition = ctx.Position + sequenceLength;
        long requestId = ctx.DecodeLong();

        using ArrayPoolList<Hash256> hashes = ctx.DecodeArrayPoolList(static (ref RlpReader c) => DecodeTransactionHash(ref c), limit: HashesRlpLimit);

        int cellsSequenceLength = ctx.ReadSequenceLength();
        if (cellsSequenceLength > Eth72ProtocolHandler.MinCellsResponseBytes)
        {
            throw new RlpLimitException($"Too much cell data in {nameof(CellsMessage72)}: {cellsSequenceLength}.");
        }

        int cellsEnd = ctx.Position + cellsSequenceLength;
        List<byte[][]> cellsByTx = new(hashes.Count);
        while (ctx.Position < cellsEnd)
        {
            if (cellsByTx.Count >= hashes.Count)
            {
                throw new RlpException($"Too many cell groups in {nameof(CellsMessage72)}. Expected {hashes.Count}.");
            }

            cellsByTx.Add(ctx.DecodeByteArrays(CellsPerTransactionRlpLimit, innerSize: Ckzg.BytesPerCell));
        }

        byte[] cellMask = ctx.DecodeByteArray(size: BlobCellMask.FixedByteLength);

        ctx.Check(checkPosition);
        if (cellsByTx.Count != hashes.Count)
        {
            throw new RlpException($"Wrong number of cell groups in {nameof(CellsMessage72)}. Expected {hashes.Count}, got {cellsByTx.Count}.");
        }

        int cellIndexCount = BlobCellMask.FromBytes(cellMask).Count;
        for (int i = 0; i < cellsByTx.Count; i++)
        {
            cellsByTx[i] = ToBlobMajor(cellsByTx[i], cellIndexCount);
        }

        return new CellsMessage72(requestId, hashes.AsSpan().ToArray(), cellsByTx.ToArray(), cellMask);
    }

    private static Hash256 DecodeTransactionHash(ref RlpReader ctx) =>
        ctx.DecodeKeccak() ?? throw new RlpException($"Null transaction hash in {nameof(CellsMessage72)}.");

    public int GetLength(CellsMessage72 message, out int contentLength)
    {
        contentLength = Rlp.LengthOf(message.RequestId)
            + Rlp.LengthOfSequence(Rlp.LengthOf(message.Hashes))
            + Rlp.LengthOfSequence(GetCellsContentLength(message.Cells))
            + Rlp.LengthOf(message.CellMask);
        return Rlp.LengthOfSequence(contentLength);
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

    private static void EncodeCellGroup(ref ByteBufferRlpWriter writer, byte[][] blobMajorCells, int cellIndexCount)
    {
        int contentLength = 0;
        for (int i = 0; i < blobMajorCells.Length; i++)
        {
            contentLength += Rlp.LengthOf(blobMajorCells[i]);
        }

        writer.StartSequence(contentLength);
        if (cellIndexCount == 0 || blobMajorCells.Length % cellIndexCount != 0)
        {
            for (int i = 0; i < blobMajorCells.Length; i++)
            {
                writer.Encode(blobMajorCells[i]);
            }

            return;
        }

        int blobCount = blobMajorCells.Length / cellIndexCount;
        for (int cellIndex = 0; cellIndex < cellIndexCount; cellIndex++)
        {
            for (int blobIndex = 0; blobIndex < blobCount; blobIndex++)
            {
                writer.Encode(blobMajorCells[blobIndex * cellIndexCount + cellIndex]);
            }
        }
    }

    private static byte[][] ToBlobMajor(byte[][] indexMajorCells, int cellIndexCount)
    {
        if (cellIndexCount == 0 || indexMajorCells.Length == 0 || indexMajorCells.Length % cellIndexCount != 0)
        {
            throw new RlpException($"Cell group in {nameof(CellsMessage72)} is inconsistent with its cell mask.");
        }

        int blobCount = indexMajorCells.Length / cellIndexCount;
        if (blobCount > Eip7594Constants.MaxBlobsPerTx)
        {
            throw new RlpLimitException($"Too many blobs in {nameof(CellsMessage72)} cell group: {blobCount}.");
        }

        if (blobCount == 1 || cellIndexCount == 1)
        {
            return indexMajorCells;
        }

        byte[][] blobMajorCells = new byte[indexMajorCells.Length][];
        for (int cellIndex = 0; cellIndex < cellIndexCount; cellIndex++)
        {
            for (int blobIndex = 0; blobIndex < blobCount; blobIndex++)
            {
                blobMajorCells[blobIndex * cellIndexCount + cellIndex] = indexMajorCells[cellIndex * blobCount + blobIndex];
            }
        }

        return blobMajorCells;
    }
}
