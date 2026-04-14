// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.SyncLimits;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V72.Messages;

public class NewPooledTransactionHashesMessageSerializer72 : IZeroMessageSerializer<NewPooledTransactionHashesMessage72>
{
    private static readonly RlpLimit TypesRlpLimit = RlpLimit.For<NewPooledTransactionHashesMessage72>(NethermindSyncLimits.MaxHashesFetch, nameof(NewPooledTransactionHashesMessage72.Types));
    private static readonly RlpLimit SizesRlpLimit = RlpLimit.For<NewPooledTransactionHashesMessage72>(NethermindSyncLimits.MaxHashesFetch, nameof(NewPooledTransactionHashesMessage72.Sizes));
    private static readonly RlpLimit HashesRlpLimit = RlpLimit.For<NewPooledTransactionHashesMessage72>(NethermindSyncLimits.MaxHashesFetch, nameof(NewPooledTransactionHashesMessage72.Hashes));

    public NewPooledTransactionHashesMessage72 Deserialize(IByteBuffer byteBuffer) =>
        byteBuffer.DeserializeRlp(Deserialize);

    private static NewPooledTransactionHashesMessage72 Deserialize(ref Rlp.ValueDecoderContext ctx)
    {
        ctx.ReadSequenceLength();
        byte[] types = ctx.DecodeByteArraySpan(TypesRlpLimit).ToArray();
        using ArrayPoolList<int> sizes = ctx.DecodeArrayPoolList(static (ref Rlp.ValueDecoderContext c) => c.DecodeInt(), limit: SizesRlpLimit);
        using ArrayPoolList<Hash256> hashes = ctx.DecodeArrayPoolList(static (ref Rlp.ValueDecoderContext c) => c.DecodeKeccak(), limit: HashesRlpLimit);
        byte[] cellMask = ctx.PeekNumberOfItemsRemaining(maxSearch: 1) == 1 ? ctx.DecodeByteArraySpan().ToArray() : [];
        return new NewPooledTransactionHashesMessage72(types, sizes.AsSpan().ToArray(), hashes.AsSpan().ToArray(), cellMask);
    }

    public void Serialize(IByteBuffer byteBuffer, NewPooledTransactionHashesMessage72 message)
    {
        int sizesLength = 0;
        foreach (int size in message.Sizes)
        {
            sizesLength += Rlp.LengthOf(size);
        }

        int hashesLength = 0;
        foreach (Hash256 hash in message.Hashes)
        {
            hashesLength += Rlp.LengthOf(hash);
        }

        int totalSize = Rlp.LengthOf(message.Types)
                        + Rlp.LengthOfSequence(sizesLength)
                        + Rlp.LengthOfSequence(hashesLength)
                        + Rlp.LengthOf(message.CellMask);

        byteBuffer.EnsureWritable(totalSize);

        RlpStream rlpStream = new NettyRlpStream(byteBuffer);
        rlpStream.StartSequence(totalSize);
        rlpStream.Encode(message.Types);

        rlpStream.StartSequence(sizesLength);
        foreach (int size in message.Sizes)
        {
            rlpStream.Encode(size);
        }

        rlpStream.StartSequence(hashesLength);
        foreach (Hash256 hash in message.Hashes)
        {
            rlpStream.Encode(hash);
        }

        rlpStream.Encode(message.CellMask);
    }
}
