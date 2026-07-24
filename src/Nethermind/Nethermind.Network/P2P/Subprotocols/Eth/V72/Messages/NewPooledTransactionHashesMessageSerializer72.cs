// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V72.Messages;

public class NewPooledTransactionHashesMessageSerializer72 : IZeroMessageSerializer<NewPooledTransactionHashesMessage72>
{
    private static readonly RlpLimit TypesRlpLimit = RlpLimit.For<NewPooledTransactionHashesMessage72>(NewPooledTransactionHashesMessage72.MaxCount, nameof(NewPooledTransactionHashesMessage72.Types));
    private static readonly RlpLimit SizesRlpLimit = RlpLimit.For<NewPooledTransactionHashesMessage72>(NewPooledTransactionHashesMessage72.MaxCount, nameof(NewPooledTransactionHashesMessage72.Sizes));
    private static readonly RlpLimit HashesRlpLimit = RlpLimit.For<NewPooledTransactionHashesMessage72>(NewPooledTransactionHashesMessage72.MaxCount, nameof(NewPooledTransactionHashesMessage72.Hashes));
    private static readonly RlpLimit CellMaskRlpLimit = RlpLimit.For<NewPooledTransactionHashesMessage72>(BlobCellMask.FixedByteLength, nameof(NewPooledTransactionHashesMessage72.CellMask));

    public NewPooledTransactionHashesMessage72 Deserialize(IByteBuffer byteBuffer) =>
        byteBuffer.DeserializeRlp(Deserialize);

    private static NewPooledTransactionHashesMessage72 Deserialize(ref RlpReader ctx)
    {
        int sequenceLength = ctx.ReadSequenceLength();
        int checkPosition = ctx.Position + sequenceLength;
        ArrayPoolList<byte>? types = null;
        ArrayPoolList<int>? sizes = null;
        ArrayPoolList<Hash256>? hashes = null;

        try
        {
            types = ctx.DecodeByteArraySpan(TypesRlpLimit).ToPooledList();
            sizes = ctx.DecodeArrayPoolList(static (ref RlpReader c) => DecodeTransactionSize(ref c), limit: SizesRlpLimit);
            hashes = ctx.DecodeArrayPoolList(static (ref RlpReader c) => DecodeTransactionHash(ref c), limit: HashesRlpLimit);
            if (ctx.PeekNumberOfItemsRemaining(checkPosition, maxSearch: 2) != 1)
            {
                throw new RlpException($"Wrong format of {nameof(NewPooledTransactionHashesMessage72)} message. Expected exactly one cell mask field.");
            }

            ReadOnlySpan<byte> cellMaskSpan = ctx.DecodeByteArraySpan(CellMaskRlpLimit);
            ctx.Check(checkPosition);
            if (cellMaskSpan.Length != BlobCellMask.FixedByteLength)
            {
                throw new RlpException($"Invalid cell mask length in {nameof(NewPooledTransactionHashesMessage72)}: expected {BlobCellMask.FixedByteLength}, got {cellMaskSpan.Length}.");
            }

            if (types.Count != sizes.Count || types.Count != hashes.Count)
            {
                throw new RlpException(
                    $"Mismatched field counts in {nameof(NewPooledTransactionHashesMessage72)}: "
                    + $"{types.Count} types, {sizes.Count} sizes, {hashes.Count} hashes.");
            }

            NewPooledTransactionHashesMessage72 message = new(types, sizes, hashes, cellMaskSpan.ToArray());
            types = null;
            sizes = null;
            hashes = null;
            return message;
        }
        finally
        {
            types?.Dispose();
            sizes?.Dispose();
            hashes?.Dispose();
        }
    }

    private static int DecodeTransactionSize(ref RlpReader ctx)
    {
        int size = ctx.DecodePositiveInt();
        if (size == 0)
        {
            throw new RlpException($"{nameof(NewPooledTransactionHashesMessage72)} transaction size must be positive.");
        }

        return size;
    }

    private static Hash256 DecodeTransactionHash(ref RlpReader ctx) =>
        ctx.DecodeKeccak() ?? throw new RlpException($"Null transaction hash in {nameof(NewPooledTransactionHashesMessage72)}.");

    public void Serialize(IByteBuffer byteBuffer, NewPooledTransactionHashesMessage72 message)
    {
        int sizesLength = 0;
        foreach (int size in message.Sizes.AsSpan())
        {
            sizesLength += Rlp.LengthOf(size);
        }

        int hashesLength = 0;
        foreach (Hash256 hash in message.Hashes.AsSpan())
        {
            hashesLength += Rlp.LengthOf(hash);
        }

        int contentLength = Rlp.LengthOf(message.Types.AsSpan())
                            + Rlp.LengthOfSequence(sizesLength)
                            + Rlp.LengthOfSequence(hashesLength)
                            + Rlp.LengthOf(message.CellMask);

        byteBuffer.EnsureWritable(Rlp.LengthOfSequence(contentLength));

        ByteBufferRlpWriter writer = new(byteBuffer);
        writer.StartSequence(contentLength);
        writer.Encode(message.Types.AsSpan());

        writer.StartSequence(sizesLength);
        foreach (int size in message.Sizes.AsSpan())
        {
            writer.Encode(size);
        }

        writer.StartSequence(hashesLength);
        foreach (Hash256 hash in message.Hashes.AsSpan())
        {
            writer.Encode(hash);
        }

        writer.Encode(message.CellMask);
    }
}
