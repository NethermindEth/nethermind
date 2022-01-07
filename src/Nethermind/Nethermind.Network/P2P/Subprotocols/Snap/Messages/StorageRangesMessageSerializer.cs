//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using DotNetty.Buffers;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Snap.Messages
{
    public class StorageRangesMessageSerializer : IZeroMessageSerializer<StorageRangesMessage>
    {
        public void Serialize(IByteBuffer byteBuffer, StorageRangesMessage message)
        {
            int contentLength = CalculateLengths(message);
            byteBuffer.EnsureWritable(Rlp.LengthOfSequence(contentLength), true);
            NettyRlpStream stream = new (byteBuffer);
            stream.StartSequence(contentLength);
            
            stream.Encode(message.RequestId);
            
            if (message.Slots == null || message.Slots.Length == 0)
            {
                stream.EncodeNullObject();
            }
            else
            {
                stream.StartSequence(message.Slots.RlpLength.Value);
                for (int i = 0; i < message.Slots.Length; i++)
                {
                    var accountSlots = message.Slots.Array[i];
                    stream.StartSequence(accountSlots.RlpLength.Value);
                    for (int j = 0; j < accountSlots.Length; j++)
                    {
                        var slot = accountSlots.Array[j];
                        
                        stream.StartSequence(slot.RlpLength.Value);
                        stream.Encode(slot.Hash);
                        stream.Encode(slot.Data);
                    }
                    
                }
            }
            
            if (message.Proof == null || message.Proof.Length == 0)
            {
                stream.EncodeNullObject();
            }
            else
            {
                stream.StartSequence(message.Proof.RlpLength.Value);
                for (int i = 0; i < message.Proof.Length; i++)
                {
                    stream.Encode(message.Proof.Array[i]);
                }
            }
        }

        public StorageRangesMessage Deserialize(IByteBuffer byteBuffer)
        {
            StorageRangesMessage message = new();
            NettyRlpStream stream = new (byteBuffer);
            
            stream.ReadSequenceLength();

            message.RequestId = stream.DecodeLong();
            message.Slots = new MeasuredArray<MeasuredArray<Slot>>(stream.DecodeArray(DecodeAccountSlots));
            message.Proof = new MeasuredArray<byte[]>(stream.DecodeArray(s => s.DecodeByteArray()));

            return message;
        }

        private MeasuredArray<Slot> DecodeAccountSlots(RlpStream stream)
        {
            var accountSlots = stream.DecodeArray(s =>
            {
                Slot slot = new();
                stream.ReadSequenceLength();
                slot.Hash = s.DecodeKeccak();
                slot.Data = s.DecodeByteArray();
                return slot;
            });
            
            return new MeasuredArray<Slot>(accountSlots);
        }
        
        private int CalculateLengths(StorageRangesMessage message)
        {
            int contentLength = Rlp.LengthOf(message.RequestId);

            int allSlotsLength = 0;
            if (message.Slots != null)
            {
                for (var i = 0; i < message.Slots.Length; i++)
                {
                    int accountLength = 0;
                    MeasuredArray<Slot> accountSlots = message.Slots.Array[i];
                    foreach (Slot slot in accountSlots.Array)
                    {
                        int slotLength = Rlp.LengthOf(slot.Hash) + Rlp.LengthOf(slot.Data);
                        slot.RlpLength = slotLength;
                        accountLength += Rlp.LengthOfSequence(slotLength);
                    }

                    accountSlots.RlpLength = accountLength;
                    allSlotsLength += Rlp.LengthOfSequence(accountLength);
                }

                message.Slots.RlpLength = allSlotsLength;
            }

            contentLength += Rlp.LengthOfSequence(allSlotsLength);

            int proofLength = 0;
            if (message.Proof != null)
            {
                for (int i = 0; i < message.Proof.Length; i++)
                {
                    message.Proof.RlpLength = Rlp.LengthOf(message.Proof.Array[i]);
                    proofLength += message.Proof.RlpLength.Value;
                }
            }

            contentLength += Rlp.LengthOfSequence(proofLength);
            
            return contentLength;
        }
    }
}
