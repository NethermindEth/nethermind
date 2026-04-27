// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.SyncLimits;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V72.Messages;

public class CellsMessageSerializer72 : IZeroMessageSerializer<CellsMessage72>
{
    private const int MaxCellsPerTransaction = BlobCellMask.CellCount * Eip7594Constants.MaxBlobsPerTx;
    private static readonly RlpLimit HashesRlpLimit = RlpLimit.For<CellsMessage72>(NethermindSyncLimits.MaxHashesFetch, nameof(CellsMessage72.Hashes));

    public CellsMessage72 Deserialize(IByteBuffer byteBuffer) =>
        byteBuffer.DeserializeRlp(Deserialize);

    private static CellsMessage72 Deserialize(ref Rlp.ValueDecoderContext ctx)
    {
        ctx.ReadSequenceLength();
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
        if (cellsByTx.Count != hashes.Count)
        {
            throw new RlpException($"Wrong format of {nameof(CellsMessage72)} message. Hashes count: {hashes.Count} Cells count: {cellsByTx.Count}.");
        }

        return new CellsMessage72(hashes.AsSpan().ToArray(), cellsByTx.ToArray(), cellMask);
    }

    public void Serialize(IByteBuffer byteBuffer, CellsMessage72 message)
    {
        int hashesLength = 0;
        foreach (Hash256 hash in message.Hashes)
        {
            hashesLength += Rlp.LengthOf(hash);
        }

        int cellsLength = 0;
        foreach (byte[][] cells in message.Cells)
        {
            cellsLength += Rlp.LengthOf(cells);
        }

        int totalSize = Rlp.LengthOfSequence(hashesLength)
                        + Rlp.LengthOfSequence(cellsLength)
                        + Rlp.LengthOf(message.CellMask);
        byteBuffer.EnsureWritable(totalSize);

        RlpStream rlpStream = new NettyRlpStream(byteBuffer);
        rlpStream.StartSequence(totalSize);

        rlpStream.StartSequence(hashesLength);
        foreach (Hash256 hash in message.Hashes)
        {
            rlpStream.Encode(hash);
        }

        rlpStream.StartSequence(cellsLength);
        foreach (byte[][] cells in message.Cells)
        {
            rlpStream.Encode(cells);
        }

        rlpStream.Encode(message.CellMask);
    }
}
