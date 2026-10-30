// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using CkzgLib;
using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V72.Messages;

public class CellsMessageSerializer72 : IZeroInnerMessageSerializer<CellsMessage72>
{
    private static readonly RlpLimit HashesRlpLimit = RlpLimit.For<CellsMessage72>(Eth72ProtocolHandler.MaxCellsResponseHashes, nameof(CellsMessage72.Hashes));
    private readonly int _maxBlobsPerTransaction;
    private readonly RlpLimit _cellsPerTransactionRlpLimit;

    public CellsMessageSerializer72()
        : this(BlobCellMask.CellCount * Eip7594Constants.MaxBlobsPerTx)
    {
    }

    public CellsMessageSerializer72(ISpecProvider specProvider)
        : this(GetMaxCellsPerTransaction(specProvider))
    {
    }

    private CellsMessageSerializer72(int maxCellsPerTransaction)
    {
        _maxBlobsPerTransaction = maxCellsPerTransaction / BlobCellMask.CellCount;
        _cellsPerTransactionRlpLimit = RlpLimit.For<CellsMessage72>(maxCellsPerTransaction, nameof(CellsMessage72.Cells));
    }

    public void Serialize(IByteBuffer byteBuffer, CellsMessage72 message) => this.SerializeWithLengths(byteBuffer, message);

    public void Serialize(IByteBuffer byteBuffer, CellsMessage72 message, ref LengthList lengths)
    {
        ByteBufferRlpWriter writer = new(byteBuffer);
        writer.StartSequence(lengths.Read());
        writer.Encode(message.RequestId);

        int hashesLength = checked(message.Hashes.Length * Rlp.LengthOfKeccakRlp);
        writer.StartSequence(hashesLength);

        foreach (ValueHash256 hash in message.Hashes)
        {
            writer.Encode(hash);
        }

        writer.StartSequence(lengths.Read());

        int cellIndexCount = message.CellMask.Length == BlobCellMask.FixedByteLength
            ? BlobCellMask.FromBytes(message.CellMask).Count
            : 0;
        foreach (byte[][] cells in message.Cells)
        {
            EncodeCellGroup(ref writer, cells, cellIndexCount, lengths.Read());
        }

        writer.Encode(message.CellMask);
    }

    public CellsMessage72 Deserialize(IByteBuffer byteBuffer) => byteBuffer.DeserializeRlp(Deserialize);

    private CellsMessage72 Deserialize(ref RlpReader ctx)
    {
        int sequenceLength = ctx.ReadSequenceLength();
        int checkPosition = ctx.Position + sequenceLength;
        long requestId = ctx.DecodeLong();

        using ArrayPoolList<ValueHash256> hashes = ctx.DecodeNonNullArrayPoolList(static (ref RlpReader c) => DecodeTransactionHash(ref c), limit: HashesRlpLimit);

        int cellsSequenceLength = ctx.ReadSequenceLength();
        if (cellsSequenceLength > Eth72ProtocolHandler.MaxCellsMessageBytes)
        {
            throw new RlpLimitException($"Too much cell data in {nameof(CellsMessage72)}: {cellsSequenceLength}.");
        }

        int cellsEnd = ctx.Position + cellsSequenceLength;
        byte[][][] cellsByTx = hashes.Count == 0 ? [] : new byte[hashes.Count][][];
        int cellGroupCount = 0;
        while (ctx.Position < cellsEnd)
        {
            if (cellGroupCount >= hashes.Count)
            {
                throw new RlpException($"Too many cell groups in {nameof(CellsMessage72)}. Expected {hashes.Count}.");
            }

            cellsByTx[cellGroupCount] = ctx.DecodeByteArrays(_cellsPerTransactionRlpLimit, innerSize: Ckzg.BytesPerCell);
            cellGroupCount++;
        }

        byte[] cellMask = ctx.DecodeByteArray(size: BlobCellMask.FixedByteLength);

        ctx.Check(checkPosition);
        if (cellGroupCount != hashes.Count)
        {
            throw new RlpException($"Wrong number of cell groups in {nameof(CellsMessage72)}. Expected {hashes.Count}, got {cellGroupCount}.");
        }

        int cellIndexCount = BlobCellMask.FromBytes(cellMask).Count;
        for (int i = 0; i < cellsByTx.Length; i++)
        {
            cellsByTx[i] = ToBlobMajor(cellsByTx[i], cellIndexCount);
        }

        return new CellsMessage72(requestId, hashes.AsSpan().ToArray(), cellsByTx, cellMask);
    }

    private static ValueHash256 DecodeTransactionHash(ref RlpReader ctx) => ctx.DecodeValueKeccakNonNull();

    public void PopulateLength(CellsMessage72 message, in LengthListWriter lengths)
    {
        using LengthListWriter sequence = lengths.BeginContainer();
        sequence.Add(Rlp.LengthOf(message.RequestId)
            + Rlp.LengthOfSequence(checked(message.Hashes.Length * Rlp.LengthOfKeccakRlp))
            + Rlp.LengthOf(message.CellMask));

        byte[][][] cellsByTx = message.Cells;
        using LengthListWriter cells = sequence.BeginContainer();
        for (int i = 0; i < cellsByTx.Length; i++)
        {
            using LengthListWriter cellGroup = cells.BeginContainer();
            cellGroup.Add(GetCellGroupContentLength(cellsByTx[i]));
        }
    }

    private static int GetCellGroupContentLength(byte[][] blobMajorCells)
    {
        int contentLength = 0;
        for (int i = 0; i < blobMajorCells.Length; i++)
        {
            contentLength += Rlp.LengthOf(blobMajorCells[i]);
        }

        return contentLength;
    }

    private static void EncodeCellGroup(ref ByteBufferRlpWriter writer, byte[][] blobMajorCells, int cellIndexCount, int contentLength)
    {
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

    private byte[][] ToBlobMajor(byte[][] indexMajorCells, int cellIndexCount)
    {
        if (cellIndexCount == 0 || indexMajorCells.Length == 0 || indexMajorCells.Length % cellIndexCount != 0)
        {
            throw new RlpException($"Cell group in {nameof(CellsMessage72)} is inconsistent with its cell mask.");
        }

        int blobCount = indexMajorCells.Length / cellIndexCount;
        if (blobCount > _maxBlobsPerTransaction)
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

    private static int GetMaxCellsPerTransaction(ISpecProvider specProvider)
    {
        ArgumentNullException.ThrowIfNull(specProvider);
        return Eth72ProtocolHandler.GetMaxCellsPerTransaction(specProvider);
    }
}
