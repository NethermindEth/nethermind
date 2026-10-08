// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Network.P2P.Subprotocols.Eth.V72.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V73.Messages;

/// <summary>
/// Serializes <c>[txtypes, [txsize, ...], [txhash, ...], cells, [txsource, ...], [txnonce, ...]]</c>.
/// </summary>
/// <remarks>
/// EIP-8077 appends the source and nonce lists to the announcement of the preceding protocol version, which for eth/73 is
/// the eth/72 one carrying the EIP-8070 cell mask.
/// </remarks>
public class NewPooledTransactionHashesMessageSerializer73 : IZeroMessageSerializer<NewPooledTransactionHashesMessage73>
{
    private static readonly RlpLimit TypesRlpLimit = RlpLimit.For<NewPooledTransactionHashesMessage73>(NewPooledTransactionHashesMessage72.MaxCount, nameof(NewPooledTransactionHashesMessage73.Types));
    private static readonly RlpLimit SizesRlpLimit = RlpLimit.For<NewPooledTransactionHashesMessage73>(NewPooledTransactionHashesMessage72.MaxCount, nameof(NewPooledTransactionHashesMessage73.Sizes));
    private static readonly RlpLimit HashesRlpLimit = RlpLimit.For<NewPooledTransactionHashesMessage73>(NewPooledTransactionHashesMessage72.MaxCount, nameof(NewPooledTransactionHashesMessage73.Hashes));
    private static readonly RlpLimit CellMaskRlpLimit = RlpLimit.For<NewPooledTransactionHashesMessage73>(BlobCellMask.FixedByteLength, nameof(NewPooledTransactionHashesMessage73.CellMask));
    private static readonly RlpLimit SourcesRlpLimit = RlpLimit.For<NewPooledTransactionHashesMessage73>(NewPooledTransactionHashesMessage72.MaxCount, nameof(NewPooledTransactionHashesMessage73.Sources));
    private static readonly RlpLimit NoncesRlpLimit = RlpLimit.For<NewPooledTransactionHashesMessage73>(NewPooledTransactionHashesMessage72.MaxCount, nameof(NewPooledTransactionHashesMessage73.Nonces));

    public NewPooledTransactionHashesMessage73 Deserialize(IByteBuffer byteBuffer) =>
        byteBuffer.DeserializeRlp(Deserialize);

    private static NewPooledTransactionHashesMessage73 Deserialize(ref RlpReader ctx)
    {
        int sequenceLength = ctx.ReadSequenceLength();
        int checkPosition = ctx.Position + sequenceLength;
        ArrayPoolList<byte>? types = null;
        ArrayPoolList<int>? sizes = null;
        ArrayPoolList<ValueHash256>? hashes = null;
        ArrayPoolList<Address>? sources = null;
        ArrayPoolList<ulong>? nonces = null;

        try
        {
            types = ctx.DecodeByteArraySpan(TypesRlpLimit).ToPooledList();
            sizes = ctx.DecodeNonNullArrayPoolList(static (ref RlpReader c) => NewPooledTransactionHashesMessageSerializer72.DecodeTransactionSize(ref c), limit: SizesRlpLimit);
            hashes = ctx.DecodeNonNullArrayPoolList(static (ref RlpReader c) => NewPooledTransactionHashesMessageSerializer72.DecodeTransactionHash(ref c), limit: HashesRlpLimit);
            if (ctx.PeekNumberOfItemsRemaining(checkPosition, maxSearch: 4) != 3)
            {
                throw new RlpException($"Wrong format of {nameof(NewPooledTransactionHashesMessage73)} message. Expected cell mask, sources and nonces fields.");
            }

            byte[] cellMask = ctx.DecodeByteArraySpan(CellMaskRlpLimit).ToArray();
            if (cellMask.Length != BlobCellMask.FixedByteLength)
            {
                throw new RlpException($"Invalid cell mask length in {nameof(NewPooledTransactionHashesMessage73)}: expected {BlobCellMask.FixedByteLength}, got {cellMask.Length}.");
            }

            sources = ctx.DecodeNonNullArrayPoolList(static (ref RlpReader c) => c.DecodeAddress(), limit: SourcesRlpLimit);
            nonces = ctx.DecodeNonNullArrayPoolList(static (ref RlpReader c) => c.DecodeULong(), limit: NoncesRlpLimit);
            ctx.Check(checkPosition);

            int count = types.Count;
            if (sizes.Count != count || hashes.Count != count || sources.Count != count || nonces.Count != count)
            {
                throw new RlpException(
                    $"Mismatched field counts in {nameof(NewPooledTransactionHashesMessage73)}: "
                    + $"{types.Count} types, {sizes.Count} sizes, {hashes.Count} hashes, {sources.Count} sources, {nonces.Count} nonces.");
            }

            NewPooledTransactionHashesMessage73 message = new(types, sizes, hashes, cellMask, sources, nonces);
            types = null;
            sizes = null;
            hashes = null;
            sources = null;
            nonces = null;
            return message;
        }
        finally
        {
            types?.Dispose();
            sizes?.Dispose();
            hashes?.Dispose();
            sources?.Dispose();
            nonces?.Dispose();
        }
    }

    public void Serialize(IByteBuffer byteBuffer, NewPooledTransactionHashesMessage73 message)
    {
        int sizesLength = 0;
        foreach (int size in message.Sizes.AsSpan())
        {
            sizesLength += Rlp.LengthOf(size);
        }

        int hashesLength = checked(message.Hashes.Count * Rlp.LengthOfKeccakRlp);
        int sourcesLength = checked(message.Sources.Count * Rlp.LengthOfAddressRlp);

        int noncesLength = 0;
        foreach (ulong nonce in message.Nonces.AsSpan())
        {
            noncesLength += Rlp.LengthOf(nonce);
        }

        int contentLength = Rlp.LengthOf(message.Types.AsSpan())
                            + Rlp.LengthOfSequence(sizesLength)
                            + Rlp.LengthOfSequence(hashesLength)
                            + Rlp.LengthOf(message.CellMask)
                            + Rlp.LengthOfSequence(sourcesLength)
                            + Rlp.LengthOfSequence(noncesLength);

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
        foreach (ValueHash256 hash in message.Hashes.AsSpan())
        {
            writer.Encode(hash);
        }

        writer.Encode(message.CellMask);

        writer.StartSequence(sourcesLength);
        foreach (Address source in message.Sources.AsSpan())
        {
            writer.Encode(source);
        }

        writer.StartSequence(noncesLength);
        foreach (ulong nonce in message.Nonces.AsSpan())
        {
            writer.Encode(nonce);
        }
    }
}
