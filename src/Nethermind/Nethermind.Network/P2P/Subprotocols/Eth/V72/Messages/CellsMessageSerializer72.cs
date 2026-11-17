// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using CkzgLib;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V72.Messages;

public class CellsMessageSerializer72 : IMessageSerializer<CellsMessage72>
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

    public int Serialize(Span<byte> destination, CellsMessage72 message)
    {
        RlpWriter writer = RlpWriter.ContentWriter(destination);
        RlpSequence sequence = writer.StartSequence(GetContentLength(message));
        writer.Encode(message.RequestId);

        int hashesLength = checked(message.Hashes.Length * Rlp.LengthOfKeccakRlp);
        RlpSequence hashes = writer.StartSequence(hashesLength);

        foreach (ValueHash256 hash in message.Hashes)
        {
            writer.Encode(hash);
        }

        writer.EndSequence(hashes);
        int cellsLength = GetCellsContentLength(message.Cells);
        RlpSequence cellGroups = writer.StartSequence(cellsLength);

        foreach (byte[][] cells in message.Cells)
        {
            EncodeCellGroup(ref writer, cells);
        }

        writer.EndSequence(cellGroups);
        writer.Encode(message.CellMask);
        writer.EndSequence(sequence);
        return writer.Length;
    }

    public CellsMessage72 Deserialize(ReadOnlySpan<byte> data)
    {
        RlpReader ctx = new(data);
        CellsMessage72 message = Deserialize(ref ctx);
        ctx.CheckEnd();
        return message;
    }

    private CellsMessage72 Deserialize(ref RlpReader reader)
    {
        RlpReader ctx = reader.EnterSequence(out int itemLength);
        long requestId = ctx.DecodeLong();

        using ArrayPoolList<ValueHash256> hashes = ctx.DecodeNonNullArrayPoolList(static (ref RlpReader c) => DecodeTransactionHash(ref c), limit: HashesRlpLimit);

        RlpReader cellsReader = ctx.EnterSequence(out int cellsItemLength);
        int cellsSequenceLength = cellsReader.Remaining;
        if (cellsSequenceLength > Eth72ProtocolHandler.MaxCellsMessageBytes)
        {
            throw new RlpLimitException($"Too much cell data in {nameof(CellsMessage72)}: {cellsSequenceLength}.");
        }

        byte[][][] cellsByTx = hashes.Count == 0 ? [] : new byte[hashes.Count][][];
        int cellGroupCount = 0;
        while (cellsReader.HasNext)
        {
            if (cellGroupCount >= hashes.Count)
            {
                throw new RlpReaderException($"Too many cell groups in {nameof(CellsMessage72)}. Expected {hashes.Count}.");
            }

            cellsByTx[cellGroupCount] = cellsReader.DecodeByteArrays(_cellsPerTransactionRlpLimit, innerSize: Ckzg.BytesPerCell);
            cellGroupCount++;
        }

        ctx.ExitSequence(cellsReader, cellsItemLength);
        byte[] cellMask = ctx.DecodeByteArray(size: BlobCellMask.FixedByteLength);

        reader.ExitSequence(ctx, itemLength);
        if (cellGroupCount != hashes.Count)
        {
            throw new RlpReaderException($"Wrong number of cell groups in {nameof(CellsMessage72)}. Expected {hashes.Count}, got {cellGroupCount}.");
        }

        int cellIndexCount = BlobCellMask.FromBytes(cellMask).Count;
        for (int i = 0; i < cellsByTx.Length; i++)
        {
            ValidateCellGroup(cellsByTx[i], cellIndexCount);
        }

        return new CellsMessage72(requestId, hashes.AsSpan().ToArray(), cellsByTx, cellMask);
    }

    private static ValueHash256 DecodeTransactionHash(ref RlpReader ctx) => ctx.DecodeValueKeccakNonNull();

    public int GetLength(CellsMessage72 message) => Rlp.LengthOfSequence(GetContentLength(message));

    private static int GetContentLength(CellsMessage72 message) =>
        Rlp.LengthOf(message.RequestId)
        + Rlp.LengthOfSequence(checked(message.Hashes.Length * Rlp.LengthOfKeccakRlp))
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

    private static void EncodeCellGroup(ref RlpWriter writer, byte[][] cells)
    {
        int contentLength = 0;
        for (int i = 0; i < cells.Length; i++)
        {
            contentLength += Rlp.LengthOf(cells[i]);
        }

        RlpSequence sequence = writer.StartSequence(contentLength);
        for (int i = 0; i < cells.Length; i++)
        {
            writer.Encode(cells[i]);
        }

        writer.EndSequence(sequence);
    }

    private void ValidateCellGroup(byte[][] cells, int cellIndexCount)
    {
        if (cellIndexCount == 0 || cells.Length == 0 || cells.Length % cellIndexCount != 0)
        {
            throw new RlpReaderException($"Cell group in {nameof(CellsMessage72)} is inconsistent with its cell mask.");
        }

        int blobCount = cells.Length / cellIndexCount;
        if (blobCount > _maxBlobsPerTransaction)
        {
            throw new RlpLimitException($"Too many blobs in {nameof(CellsMessage72)} cell group: {blobCount}.");
        }
    }

    private static int GetMaxCellsPerTransaction(ISpecProvider specProvider)
    {
        ArgumentNullException.ThrowIfNull(specProvider);
        return Eth72ProtocolHandler.GetMaxCellsPerTransaction(specProvider);
    }
}
