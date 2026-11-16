// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Collections;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Snap.V2.Messages
{
    public class BlockAccessListsMessageSerializer : IMessageSerializer<BlockAccessListsMessage>
    {
        private const int UnavailableEntryLength = 1;

        private static readonly RlpLimit RlpLimit = RlpLimit.For<BlockAccessListsMessage>(
            SnapMessageLimits.MaxRequestHashes, nameof(BlockAccessListsMessage.BlockAccessLists));

        public int Serialize(Span<byte> destination, BlockAccessListsMessage message)
        {
            int entriesContentLength = GetEntriesContentLength(message.BlockAccessLists);

            RlpWriter writer = RlpWriter.ContentWriter(destination);
            RlpSequence sequence = writer.StartSequence(GetContentLength(message, entriesContentLength));
            writer.Encode(message.RequestId);
            RlpSequence sequence2 = writer.StartSequence(entriesContentLength);

            IByteArrayList blockAccessLists = message.BlockAccessLists;
            for (int i = 0; i < blockAccessLists.Count; i++)
            {
                WriteBlockAccessListEntry(ref writer, blockAccessLists[i]);
            }

            writer.EndSequence(sequence2);
            writer.EndSequence(sequence);
            return writer.Length;
        }

        public int GetLength(BlockAccessListsMessage message) =>
            Rlp.LengthOfSequence(GetContentLength(message, GetEntriesContentLength(message.BlockAccessLists)));

        private static int GetContentLength(BlockAccessListsMessage message, int entriesContentLength) =>
            Rlp.LengthOf(message.RequestId) + Rlp.LengthOfSequence(entriesContentLength);

        private static void WriteBlockAccessListEntry(ref RlpWriter writer, ReadOnlySpan<byte> entry)
        {
            if (entry.IsEmpty)
                writer.WriteByte(Rlp.EmptyByteArrayByte);
            else
                writer.WriteEncoded(entry);
        }

        public BlockAccessListsMessage Deserialize(ReadOnlySpan<byte> data)
        {
            RlpReader reader = new(data);
            ArrayPoolList<byte[]>? blockAccessLists = null;

            try
            {
                RlpReader ctx = reader.EnterSequence(out int itemLength);
                long requestId = ctx.DecodeLong();

                blockAccessLists = DecodeBlockAccessLists(ref ctx);
                reader.ExitSequence(ctx, itemLength);
                reader.CheckEnd();
                return new BlockAccessListsMessage(new ByteArrayListAdapter(blockAccessLists)) { RequestId = requestId };
            }
            catch
            {
                blockAccessLists?.Dispose();
                throw;
            }
        }

        private static int GetEntriesContentLength(IByteArrayList blockAccessLists)
        {
            int contentLength = 0;
            for (int i = 0; i < blockAccessLists.Count; i++)
            {
                ReadOnlySpan<byte> entry = blockAccessLists[i];
                contentLength += entry.IsEmpty ? UnavailableEntryLength : entry.Length;
            }

            return contentLength;
        }

        private static ArrayPoolList<byte[]> DecodeBlockAccessLists(ref RlpReader reader)
        {
            RlpReader ctx = reader.EnterSequence(out int itemLength);
            int entryCount = ctx.CountItems(RlpLimit);
            ArrayPoolList<byte[]> blockAccessLists = new(entryCount);

            try
            {
                while (ctx.HasNext)
                {
                    ReadOnlySpan<byte> entry = ctx.ReadEncodedItem();
                    blockAccessLists.Add(entry.Length == UnavailableEntryLength && entry[0] == Rlp.EmptyByteArrayByte
                        ? []
                        : entry.ToArray());
                }

                reader.ExitSequence(ctx, itemLength);
                return blockAccessLists;
            }
            catch
            {
                blockAccessLists.Dispose();
                throw;
            }
        }
    }
}
