// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Snap;

namespace Nethermind.Network.P2P.Subprotocols.Snap.Messages
{
    public sealed class StorageRangesMessageSerializer : IZeroMessageSerializer<StorageRangeMessage>
    {
        public void Serialize(IByteBuffer byteBuffer, StorageRangeMessage message)
        {
            (int contentLength, int allSlotsLength, int[] accountSlotsLengths, int proofsLength) = CalculateLengths(message);

            byteBuffer.EnsureWritable(Rlp.LengthOfSequence(contentLength));
            NettyRlpStream stream = new(byteBuffer);

            stream.StartSequence(contentLength);

            stream.Encode(message.RequestId);

            if (message.Slots is null || message.Slots.Count == 0)
            {
                stream.EncodeNullObject();
            }
            else
            {
                stream.StartSequence(allSlotsLength);

                for (int i = 0; i < message.Slots.Count; i++)
                {
                    stream.StartSequence(accountSlotsLengths[i]);

                    IOwnedReadOnlyList<PathWithStorageSlot> accountSlots = message.Slots[i];

                    for (int j = 0; j < accountSlots.Count; j++)
                    {
                        var slot = accountSlots[j];

                        int itemLength = Rlp.LengthOf(slot.Path) + Rlp.LengthOf(slot.SlotRlpValue);

                        stream.StartSequence(itemLength);
                        stream.Encode(slot.Path);
                        stream.Encode(slot.SlotRlpValue);
                    }

                }
            }

            if (!stream.TryWriteRlpWrapper(message.Proofs))
            {
                if (message.Proofs is null || message.Proofs.Count == 0)
                {
                    stream.EncodeNullObject();
                }
                else
                {
                    stream.StartSequence(proofsLength);
                    for (int i = 0; i < message.Proofs.Count; i++)
                    {
                        stream.Encode(message.Proofs[i]);
                    }
                }
            }
        }

        public StorageRangeMessage Deserialize(IByteBuffer byteBuffer)
        {
            NettyBufferMemoryOwner memoryOwner = new(byteBuffer);
            Rlp.ValueDecoderContext ctx = new(memoryOwner.Memory, true);
            int startPos = ctx.Position;

            ctx.ReadSequenceLength();

            StorageRangeMessage message = new();
            message.RequestId = ctx.DecodeLong();

            int slotsCheck = ctx.ReadSequenceLength() + ctx.Position;
            int slotsCount = ctx.PeekNumberOfItemsRemaining(slotsCheck);
            ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>> slots = new(slotsCount);
            for (int i = 0; i < slotsCount; i++)
            {
                int accountSlotsCheck = ctx.ReadSequenceLength() + ctx.Position;
                int accountSlotsCount = ctx.PeekNumberOfItemsRemaining(accountSlotsCheck);
                ArrayPoolList<PathWithStorageSlot> accountSlots = new(accountSlotsCount);
                for (int j = 0; j < accountSlotsCount; j++)
                {
                    ctx.ReadSequenceLength();
                    Hash256 path = ctx.DecodeKeccak();
                    byte[] value = ctx.DecodeByteArray();
                    accountSlots.Add(new PathWithStorageSlot(in path.ValueHash256, value));
                }

                slots.Add(accountSlots);
            }

            message.Slots = slots;
            message.Proofs = RlpByteArrayList.DecodeList(ref ctx, memoryOwner);

            byteBuffer.SetReaderIndex(byteBuffer.ReaderIndex + (ctx.Position - startPos));

            return message;
        }

        private static (int contentLength, int allSlotsLength, int[] accountSlotsLengths, int proofsLength) CalculateLengths(StorageRangeMessage message)
        {
            int contentLength = Rlp.LengthOf(message.RequestId);

            int allSlotsLength = 0;
            int[] accountSlotsLengths = new int[message.Slots.Count];

            if (message.Slots is null || message.Slots.Count == 0)
            {
                allSlotsLength = 1;
            }
            else
            {
                for (var i = 0; i < message.Slots.Count; i++)
                {
                    int accountSlotsLength = 0;

                    var accountSlots = message.Slots[i];
                    foreach (ref readonly PathWithStorageSlot slot in accountSlots.AsSpan())
                    {
                        int slotLength = Rlp.LengthOf(slot.Path) + Rlp.LengthOf(slot.SlotRlpValue);
                        accountSlotsLength += Rlp.LengthOfSequence(slotLength);
                    }

                    accountSlotsLengths[i] = accountSlotsLength;
                    allSlotsLength += Rlp.LengthOfSequence(accountSlotsLength);
                }
            }

            contentLength += Rlp.LengthOfSequence(allSlotsLength);

            int proofsLength = 0;
            if (message.Proofs is IRlpWrapper rlpList)
            {
                contentLength += rlpList.RlpSpan.Length;
            }
            else if (message.Proofs is null || message.Proofs.Count == 0)
            {
                proofsLength = 1;
                contentLength++;
            }
            else
            {
                for (int i = 0; i < message.Proofs.Count; i++)
                {
                    proofsLength += Rlp.LengthOf(message.Proofs[i]);
                }

                contentLength += Rlp.LengthOfSequence(proofsLength);
            }

            return (contentLength, allSlotsLength, accountSlotsLengths, proofsLength);
        }
    }
}
