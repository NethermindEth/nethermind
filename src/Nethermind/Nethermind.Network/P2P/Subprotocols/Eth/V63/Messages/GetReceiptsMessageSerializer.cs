// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.SyncLimits;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages
{
    public class GetReceiptsMessageSerializer : HashesMessageSerializer<GetReceiptsMessage>
    {
        private static readonly RlpLimit RlpLimit = RlpLimit.For<GetReceiptsMessage>(NethermindSyncLimits.MaxHashesFetch, nameof(GetReceiptsMessage.Hashes));

        public static GetReceiptsMessage Deserialize(byte[] bytes)
        {
            Rlp.ValueDecoderContext ctx = new(bytes);
            ArrayPoolList<Hash256>? hashes = ctx.DecodeArrayPoolList(static (ref Rlp.ValueDecoderContext c) => c.DecodeKeccak(), limit: RlpLimit);
            return new GetReceiptsMessage(hashes);
        }

        public override GetReceiptsMessage Deserialize(IByteBuffer byteBuffer)
        {
            Rlp.ValueDecoderContext ctx = byteBuffer.AsRlpContext();
            GetReceiptsMessage message = Deserialize(ref ctx);
            byteBuffer.SetReaderIndex(byteBuffer.ReaderIndex + ctx.Position);
            return message;
        }

        public static GetReceiptsMessage Deserialize(ref Rlp.ValueDecoderContext ctx)
        {
            ArrayPoolList<Hash256>? hashes = DeserializeHashesArrayPool(ref ctx, RlpLimit);
            return new GetReceiptsMessage(hashes);
        }
    }
}
