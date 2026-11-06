// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V72.Messages;

public class NewPooledTransactionHashesMessageSerializer72 : IMessageSerializer<NewPooledTransactionHashesMessage72>
{
    private static readonly RlpLimit TypesRlpLimit = RlpLimit.For<NewPooledTransactionHashesMessage72>(NewPooledTransactionHashesMessage72.MaxCount, nameof(NewPooledTransactionHashesMessage72.Types));
    private static readonly RlpLimit SizesRlpLimit = RlpLimit.For<NewPooledTransactionHashesMessage72>(NewPooledTransactionHashesMessage72.MaxCount, nameof(NewPooledTransactionHashesMessage72.Sizes));
    private static readonly RlpLimit HashesRlpLimit = RlpLimit.For<NewPooledTransactionHashesMessage72>(NewPooledTransactionHashesMessage72.MaxCount, nameof(NewPooledTransactionHashesMessage72.Hashes));
    private static readonly RlpLimit CellMaskRlpLimit = RlpLimit.For<NewPooledTransactionHashesMessage72>(BlobCellMask.FixedByteLength, nameof(NewPooledTransactionHashesMessage72.CellMask));

    public NewPooledTransactionHashesMessage72 Deserialize(ReadOnlySpan<byte> data)
    {
        RlpReader ctx = new(data);
        return ctx.CheckEnd(Deserialize(ref ctx));
    }

    private static NewPooledTransactionHashesMessage72 Deserialize(ref RlpReader reader)
    {
        RlpReader ctx = reader.EnterSequence(out int itemLength);
        ArrayPoolList<byte>? types = null;
        ArrayPoolList<int>? sizes = null;
        ArrayPoolList<ValueHash256>? hashes = null;

        try
        {
            types = ctx.DecodeByteArraySpan(TypesRlpLimit).ToPooledList();
            sizes = ctx.DecodeNonNullArrayPoolList(static (ref RlpReader c) => DecodeTransactionSize(ref c), limit: SizesRlpLimit);
            hashes = ctx.DecodeNonNullArrayPoolList(static (ref RlpReader c) => DecodeTransactionHash(ref c), limit: HashesRlpLimit);
            if (!ctx.HasNext)
            {
                throw new RlpReaderException($"Wrong format of {nameof(NewPooledTransactionHashesMessage72)} message. Expected exactly one cell mask field.");
            }

            ReadOnlySpan<byte> cellMaskSpan = ctx.DecodeByteArraySpan(CellMaskRlpLimit);
            if (ctx.HasNext)
            {
                throw new RlpReaderException($"Wrong format of {nameof(NewPooledTransactionHashesMessage72)} message. Expected exactly one cell mask field.");
            }

            reader.ExitSequence(ctx, itemLength);
            if (cellMaskSpan.Length != BlobCellMask.FixedByteLength)
            {
                throw new RlpReaderException($"Invalid cell mask length in {nameof(NewPooledTransactionHashesMessage72)}: expected {BlobCellMask.FixedByteLength}, got {cellMaskSpan.Length}.");
            }

            if (types.Count != sizes.Count || types.Count != hashes.Count)
            {
                throw new RlpReaderException(
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
            throw new RlpReaderException($"{nameof(NewPooledTransactionHashesMessage72)} transaction size must be positive.");
        }

        return size;
    }

    private static ValueHash256 DecodeTransactionHash(ref RlpReader ctx) => ctx.DecodeValueKeccakNonNull();

    public int Serialize(Span<byte> destination, NewPooledTransactionHashesMessage72 message)
    {
        int sizesLength = GetSizesLength(message);
        int hashesLength = GetHashesLength(message);

        RlpWriter writer = RlpWriter.ContentWriter(destination);
        RlpSequence sequence = writer.StartSequence(GetContentLength(message, sizesLength, hashesLength));
        writer.Encode(message.Types.AsSpan());

        RlpSequence sizes = writer.StartSequence(sizesLength);
        foreach (int size in message.Sizes.AsSpan())
        {
            writer.Encode(size);
        }

        writer.EndSequence(sizes);
        RlpSequence hashes = writer.StartSequence(hashesLength);
        foreach (ValueHash256 hash in message.Hashes.AsSpan())
        {
            writer.Encode(hash);
        }

        writer.EndSequence(hashes);
        writer.Encode(message.CellMask);
        writer.EndSequence(sequence);
        return writer.Length;
    }

    public int GetLength(NewPooledTransactionHashesMessage72 message) =>
        Rlp.LengthOfSequence(GetContentLength(message, GetSizesLength(message), GetHashesLength(message)));

    private static int GetContentLength(NewPooledTransactionHashesMessage72 message, int sizesLength, int hashesLength) =>
        Rlp.LengthOf(message.Types.AsSpan())
        + Rlp.LengthOfSequence(sizesLength)
        + Rlp.LengthOfSequence(hashesLength)
        + Rlp.LengthOf(message.CellMask);

    private static int GetSizesLength(NewPooledTransactionHashesMessage72 message)
    {
        int sizesLength = 0;
        foreach (int size in message.Sizes.AsSpan())
        {
            sizesLength += Rlp.LengthOf(size);
        }

        return sizesLength;
    }

    private static int GetHashesLength(NewPooledTransactionHashesMessage72 message) => checked(message.Hashes.Count * Rlp.LengthOfKeccakRlp);
}
