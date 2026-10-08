// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Collections;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Snap.V2.Messages
{
    public class BlockAccessListsMessageSerializer : IZeroMessageSerializer<BlockAccessListsMessage>
    {
        private const int UnavailableEntryLength = 1;

        private static readonly RlpLimit RlpLimit = RlpLimit.For<BlockAccessListsMessage>(
            SnapMessageLimits.MaxRequestHashes, nameof(BlockAccessListsMessage.BlockAccessLists));

        public void Serialize(Span<byte> buffer, BlockAccessListsMessage message)
        {
            int entriesContentLength = GetEntriesContentLength(message.BlockAccessLists);
            int contentLength = Rlp.LengthOf(message.RequestId) + Rlp.LengthOfSequence(entriesContentLength);

            RlpWriter writer = new(buffer);
            writer.StartSequence(contentLength);
            writer.Encode(message.RequestId);
            writer.StartSequence(entriesContentLength);

            IByteArrayList blockAccessLists = message.BlockAccessLists;
            for (int i = 0; i < blockAccessLists.Count; i++)
            {
                WriteBlockAccessListEntry(ref writer, blockAccessLists[i]);
            }
        }

        private static void WriteBlockAccessListEntry<TWriter>(ref TWriter writer, ReadOnlySpan<byte> entry)
            where TWriter : struct, IRlpWriteBackend, allows ref struct
        {
            if (entry.IsEmpty)
                writer.WriteByte(Rlp.EmptyByteArrayByte);
            else
                writer.Write(entry);
        }

        public int GetLength(BlockAccessListsMessage message, out int contentLength)
        {
            int entriesContentLength = GetEntriesContentLength(message.BlockAccessLists);
            contentLength = Rlp.LengthOf(message.RequestId) + Rlp.LengthOfSequence(entriesContentLength);
            return Rlp.LengthOfSequence(contentLength);
        }

        public BlockAccessListsMessage Deserialize(ReadOnlySpan<byte> data, out int consumed)
        {
            RlpReader ctx = new(data);
            ArrayPoolList<byte[]>? blockAccessLists = null;

            try
            {
                int sequenceLength = ctx.ReadSequenceLength();
                int checkPosition = ctx.Position + sequenceLength;
                long requestId = ctx.DecodeLong();

                blockAccessLists = DecodeBlockAccessLists(ref ctx);
                ctx.Check(checkPosition);
                consumed = ctx.Position;
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

        private static ArrayPoolList<byte[]> DecodeBlockAccessLists(ref RlpReader ctx)
        {
            int contentLength = ctx.ReadSequenceLength();
            int checkPosition = ctx.Position + contentLength;
            int entryCount = ctx.PeekNumberOfItemsRemaining(checkPosition, SnapMessageLimits.MaxRequestHashes + 1);
            Rlp.GuardLimit(entryCount, contentLength, RlpLimit);
            ArrayPoolList<byte[]> blockAccessLists = new(entryCount);

            try
            {
                while (ctx.Position < checkPosition)
                {
                    int length = ctx.PeekNextRlpLength();
                    ReadOnlySpan<byte> entry = ctx.Read(length);
                    blockAccessLists.Add(length == UnavailableEntryLength && entry[0] == Rlp.EmptyByteArrayByte
                        ? []
                        : entry.ToArray());
                }

                ctx.Check(checkPosition);
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
