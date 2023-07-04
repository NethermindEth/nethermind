// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using DotNetty.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Snap;

namespace Nethermind.Network.P2P.Subprotocols.Snap.Messages
{
    public class StorageRangesMessageSerializer : IZeroMessageSerializer<StorageRangeMessage>
    {
        public void Serialize(IByteBuffer byteBuffer, StorageRangeMessage message)
        {
            (int contentLength, int allSlotsLength, int[] accountSlotsLengths, int proofsLength) = CalculateLengths(message);

            byteBuffer.EnsureWritable(Rlp.LengthOfSequence(contentLength), true);
            NettyRlpStream stream = new(byteBuffer);

            stream.StartSequence(contentLength);

            stream.Encode(message.RequestId);

            if (message.Slots is null || message.Slots.Length == 0)
            {
                stream.EncodeNullObject();
            }
            else
            {
                stream.StartSequence(allSlotsLength);

                for (int i = 0; i < message.Slots.Length; i++)
                {
                    stream.StartSequence(accountSlotsLengths[i]);

                    PathWithStorageSlot[] accountSlots = message.Slots[i];

                    for (int j = 0; j < accountSlots.Length; j++)
                    {
                        var slot = accountSlots[j];

                        int itemLength = Rlp.LengthOf(slot.Path) + Rlp.LengthOf(slot.SlotRlpValue);

                        stream.StartSequence(itemLength);
                        stream.Encode(slot.Path);
                        stream.Encode(slot.SlotRlpValue);
                    }

                }
            }

            if (message.Proofs is null || message.Proofs.Length == 0)
            {
                stream.EncodeNullObject();
            }
            else
            {
                stream.StartSequence(proofsLength);
                for (int i = 0; i < message.Proofs.Length; i++)
                {
                    stream.Encode(message.Proofs[i]);
                }
            }
        }

        public StorageRangeMessage Deserialize(IByteBuffer byteBuffer)
        {
            StorageRangeMessage message = new();
            NettyRlpStream stream = new(byteBuffer);

            stream.ReadSequenceLength();

            message.RequestId = stream.DecodeLong();
            message.Slots = stream.DecodeArray(s => s.DecodeArray(DecodeSlot));
            message.Proofs = stream.DecodeArray(s => s.DecodeByteArray());

            return message;
        }

        private PathWithStorageSlot DecodeSlot(RlpStream stream)
        {
            stream.ReadSequenceLength();
            Keccak path = stream.DecodeKeccak();
            byte[] value = stream.DecodeByteArray();

            PathWithStorageSlot data = new(path, value);

            return data;
        }

        private (int contentLength, int allSlotsLength, int[] accountSlotsLengths, int proofsLength) CalculateLengths(StorageRangeMessage message)
        {
            int contentLength = Rlp.LengthOf(message.RequestId);

            int allSlotsLength = 0;
            int[] accountSlotsLengths = new int[message.Slots.Length];

            if (message.Slots is null || message.Slots.Length == 0)
            {
                allSlotsLength = 1;
            }
            else
            {
                for (var i = 0; i < message.Slots.Length; i++)
                {
                    int accountSlotsLength = 0;

                    var accountSlots = message.Slots[i];
                    foreach (PathWithStorageSlot slot in accountSlots)
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
            if (message.Proofs is null || message.Proofs.Length == 0)
            {
                proofsLength = 1;
                contentLength++;
            }
            else
            {
                for (int i = 0; i < message.Proofs.Length; i++)
                {
                    proofsLength += Rlp.LengthOf(message.Proofs[i]);
                }

                contentLength += Rlp.LengthOfSequence(proofsLength);
            }



            return (contentLength, allSlotsLength, accountSlotsLengths, proofsLength);
        }
    }
}
